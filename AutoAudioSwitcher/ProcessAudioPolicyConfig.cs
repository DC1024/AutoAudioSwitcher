// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using Serilog;
using System.Runtime.InteropServices;

namespace AutoAudioSwitcher;

/// <summary>
/// Reads and writes the default audio endpoint for a specific process ("per-app" routing), using the undocumented
/// WinRT interface <c>Windows.Media.Internal.AudioPolicyConfig</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only mechanism Windows exposes to bind an audio device to an individual process. Unlike
/// <c>IPolicyConfig.SetDefaultEndpoint</c> (the system-wide default used by <see cref="AudioDeviceManager"/>), it
/// takes a process ID. There is no official documentation, and Microsoft has changed the interface's IID between
/// Windows releases, so we probe a list of known IIDs and keep the first that activates.
/// </para>
/// <para>
/// Because the interface is undocumented, its vtable slot indices are hard-coded (see the <c>...Slot</c>
/// constants). The upside of matching the IID is that it doubles as a version check: a given IID belongs to one
/// OS generation, so a successful activation is evidence that the slot layout we hold is the right one. If no
/// known IID activates, per-app routing is disabled rather than risking a vtable call into arbitrary memory.
/// </para>
/// <para>
/// Based on the approach used by SoundSwitch (GPL) and the original C header by EreTIk (MIT), reimplemented here as
/// a minimal, self-contained interop so this project can stay Apache-2.0.
/// </para>
/// </remarks>
internal sealed class ProcessAudioPolicyConfig : IDisposable
{
    /// <summary>Size of a vtable slot / pointer on x64.</summary>
    private const int PointerSize = 8;

    // vtable slot indices within IAudioPolicyConfig (an IInspectable-derived interface). These are not
    // documented anywhere; the values below were verified empirically on Windows 11 by round-tripping a real
    // device binding and confirming the process actually switched endpoints (see SetProcessPlaybackEndpoint).
    private const int SetPersistedDefaultAudioEndpointSlot = 24;
    private const int ClearAllPersistedDefaultEndpointsSlot = 27;

    /// <summary>The WinRT class name. Undocumented, but stable since Windows 10.</summary>
    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";

    // EDataFlow.eRender
    private const int ERender = 0;

    /// <summary>ERole.eConsole — the role desktop apps generally play sound through.</summary>
    private const int ERoleConsole = 0;

    /// <summary>ERole.eMultimedia.</summary>
    private const int ERoleMultimedia = 1;

    /// <summary>ERole.eCommunications.</summary>
    private const int ERoleCommunications = 2;

    /// <summary>
    /// Known IIDs of <c>IAudioPolicyConfigFactory</c>, one per OS generation. Tried in order; the first one that
    /// activates wins. Values reverse-engineered from EarTrumpet / SoundSwitch.
    /// </summary>
    private static readonly (string Description, Guid Iid)[] KnownInterfaceGuids =
    [
        ("Windows 11 / 10 21H2+", new Guid("ab3d4648-e242-459f-b02f-541c70306324")),
        ("Windows 10 1809", new Guid("32aa8e18-6496-4e24-9f94-b800e7eccc45")),
        ("Windows 10 16299", new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")),
        ("Windows 10 downlevel", new Guid("f8679f50-850a-41cf-9c72-430f290290c8")),
    ];

    /// <summary>HRESULT returned when the target process has no audio session yet.</summary>
    private const int ErrorElementNotFound = unchecked((int)0x80070490);

    private readonly ILogger logger;
    private IntPtr policyConfig;
    private IntPtr vtable;
    private ComScope? com;

    public ProcessAudioPolicyConfig(ILogger logger)
    {
        this.logger = logger.ForContext<ProcessAudioPolicyConfig>();

        try
        {
            Initialize();
            logger.Information("Per-app audio routing is available on this system.");
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Per-app audio routing is unavailable; falling back gracefully.");
            Dispose();
        }
    }

    /// <summary>
    /// Whether the per-app endpoint interface was successfully initialized. If false, callers must not attempt to
    /// route audio per process.
    /// </summary>
    public bool IsUsable => policyConfig != 0;

    private void Initialize()
    {
        // WinRT activation on an uninitialized apartment is undefined behaviour, so this must come first.
        com = new ComScope();

        IntPtr className = HString.FromString(ClassName);
        try
        {
            for (int i = 0; i < KnownInterfaceGuids.Length; i++)
            {
                (string description, Guid iid) = KnownInterfaceGuids[i];

                int result = AudioSes.RoGetActivationFactory(className, ref iid, out IntPtr instance);

                // E_NOINTERFACE (0x80004002) just means "wrong OS generation"; keep looking.
                if (result < 0 || instance == 0)
                {
                    logger.Debug("IAudioPolicyConfig IID for {Description} did not activate (0x{Result:X8}).",
                        description, result);
                    continue;
                }

                policyConfig = instance;
                vtable = Marshal.ReadIntPtr(policyConfig);
                logger.Debug("Activated IAudioPolicyConfig for {Description}.", description);
                return;
            }
        }
        finally
        {
            HString.Delete(className);
        }

        throw new InvalidComObjectException(
            $"None of the known IAudioPolicyConfig interface GUIDs matched this system " +
            $"(build {Environment.OSVersion.Version}); refusing to guess the vtable layout.");
    }

    /// <summary>
    /// Binds <paramref name="processId"/>'s audio output to <paramref name="deviceId"/> for the given roles.
    /// Returns true if any role was written successfully.
    /// </summary>
    /// <remarks>
    /// <paramref name="deviceId"/> must be the plain MMDeviceAPI form (<c>{0.0.0.00000000}.{GUID}</c>) as reported
    /// by <c>IMMDevice.ID</c> — <em>not</em> the <c>\\?\SWD#MMDEVAPI#</c> token form that the global-default
    /// interface wants. This method takes the device ID as a plain null-terminated wide string; passing an
    /// HSTRING handle here is accepted by neither and yields <c>E_INVALIDARG</c>.
    /// </remarks>
    public bool SetProcessPlaybackEndpoint(string deviceId, uint processId, params int[] roles)
    {
        if (!IsUsable || string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        bool anySuccess = false;

        foreach (int role in roles)
        {
            try
            {
                var setEndpoint = GetDelegate<SetPersistedDefaultAudioEndpoint>(
                    SetPersistedDefaultAudioEndpointSlot);

                int result = setEndpoint(policyConfig, processId, ERender, role, deviceId);
                if (result == 0)
                {
                    anySuccess = true;
                }
                else if (result == ErrorElementNotFound)
                {
                    // The process has no audio session yet; not an error, just nothing to route.
                    logger.Debug("Process {ProcessId} has no audio session yet.", processId);
                }
                else
                {
                    logger.Warning("SetPersistedDefaultAudioEndpoint for process {ProcessId} returned 0x{Result:X8}.",
                        processId, result);
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Failed to set the endpoint for process {ProcessId}.", processId);
            }
        }

        return anySuccess;
    }

    /// <summary>
    /// Returns the device ID currently persisted for <paramref name="processId"/>, or null if none / on error.
    /// </summary>
    /// <remarks>
    /// This is only used for diagnostics. Reading a binding back through this interface proved unreliable
    /// across Windows builds (the accessor sits at an offset that varies with the IID generation), so
    /// <see cref="GetActiveSessionEndpoints"/> is the dependable way to observe where a process is routed.
    /// </remarks>
    public unsafe string? GetProcessPlaybackEndpoint(uint processId, int role = ERoleMultimedia)
    {
        if (!IsUsable)
        {
            return null;
        }

        IntPtr deviceIdHString = IntPtr.Zero;
        try
        {
            var getEndpoint = GetDelegate<GetPersistedDefaultAudioEndpoint>(
                SetPersistedDefaultAudioEndpointSlot + 1);

            int result = getEndpoint(policyConfig, processId, ERender, role, &deviceIdHString);
            return result == 0 ? HString.Take(deviceIdHString) : null;
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Could not read the persisted endpoint for process {ProcessId}.", processId);
            return null;
        }
        finally
        {
            HString.Delete(deviceIdHString);
        }
    }

    /// <summary>
    /// Removes all persisted per-app endpoint overrides, returning every process to the system default device.
    /// </summary>
    public void ClearAllPersistedEndpoints()
    {
        if (!IsUsable)
        {
            return;
        }

        try
        {
            var clearEndpoints = GetDelegate<ClearAllPersistedApplicationDefaultEndpoints>(
                ClearAllPersistedDefaultEndpointsSlot);

            int result = clearEndpoints(policyConfig);
            if (result < 0)
            {
                logger.Warning("ClearAllPersistedApplicationDefaultEndpoints returned 0x{Result:X8}.", result);
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to clear persisted per-app endpoints");
        }
    }

    private T GetDelegate<T>(int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, PointerSize * slot));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpoint(
        IntPtr self, uint processId, int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate int GetPersistedDefaultAudioEndpoint(
        IntPtr self, uint processId, int flow, int role, IntPtr* deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ClearAllPersistedApplicationDefaultEndpoints(IntPtr self);

    public void Dispose()
    {
        if (policyConfig != 0)
        {
            Marshal.Release(policyConfig);
            policyConfig = 0;
            vtable = 0;
        }

        com?.Dispose();
        com = null;
    }
}