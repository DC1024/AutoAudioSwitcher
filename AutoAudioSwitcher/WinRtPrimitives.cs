// Copyright (c) Max Kagamine
// Licensed under the Apache License, Version 2.0

using System.Runtime.InteropServices;

namespace AutoAudioSwitcher;

/// <summary>
/// Low-level pieces needed to activate the undocumented WinRT class
/// <c>Windows.Media.Internal.AudioPolicyConfig</c>.
/// </summary>
/// <remarks>
/// Three things are worth knowing about this file, each learned the hard way:
/// <list type="number">
/// <item>
/// <b>Use <c>RoGetActivationFactory</c>, not <c>DllGetActivationFactory</c>.</b> The latter hands back the
/// activation factory itself with no way to select the interface, and the interface IID is what actually varies
/// between Windows builds. <c>RoGetActivationFactory</c> takes the IID and does the QI for us.
/// </item>
/// <item>
/// <b><c>HSTRING</c> is a single opaque pointer</b> to a reference-counted buffer, not a
/// <c>{ length; buffer; }</c> struct. Declaring it as a struct makes the CLR push 16 bytes where the callee
/// expects an 8-byte pointer, and the callee then dereferences stack garbage — producing an access violation that
/// looks like it happened inside the P/Invoke. See <see cref="HString"/>.
/// </item>
/// <item>
/// <b>COM must be initialized before activation.</b> See <see cref="ComScope"/>.
/// </item>
/// </list>
/// </remarks>
internal static class AudioSes
{
    /// <summary>
    /// Activates a WinRT class and returns the requested interface. Exported by <c>combase.dll</c>.
    /// </summary>
    [DllImport("combase.dll", EntryPoint = "RoGetActivationFactory", ExactSpelling = true)]
    internal static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}

/// <summary>
/// Initializes COM for the current thread and guarantees it is released exactly once, even on exceptions. Without
/// this, WinRT activation can crash the process rather than fail gracefully.
/// </summary>
internal sealed class ComScope : IDisposable
{
    private const uint CoInitMultiThreaded = 0x0;
    private const uint CoInitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = -2147417850;

    private readonly bool ownsApartment;

    public ComScope(bool isApartmentThreaded = false)
    {
        int result = CoInitializeEx(IntPtr.Zero, isApartmentThreaded ? CoInitApartmentThreaded : CoInitMultiThreaded);
        ownsApartment = result >= 0;

        // RPC_E_CHANGED_MODE means somebody already initialized this thread as the other apartment type. That is
        // perfectly usable, so we accept it and just don't own the lifetime.
        if (result != RpcEChangedMode && result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public void Dispose()
    {
        if (ownsApartment)
        {
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

/// <summary>
/// Minimal WinRT <c>HSTRING</c> interop. HSTRING is a single <b>opaque, reference-counted pointer</b> to an
/// immutable length-prefixed UTF-16 buffer; it is not a struct of visible fields. Pass it around as an
/// <see cref="IntPtr"/> and release it with <c>WindowsDeleteString</c>.
/// </summary>
/// <remarks>
/// Getting this wrong is expensive: declaring it as a <c>{ int length; IntPtr buffer; }</c> struct makes the CLR
/// hand the callee 16 bytes where it expects an 8-byte pointer, and
/// <c>DllGetActivationFactory</c> then dereferences stack garbage and dies with an access violation that looks
/// like it happens inside the P/Invoke itself.
/// </remarks>
internal static class HString
{
    /// <summary>
    /// Creates an HSTRING from a managed string. The caller owns the result and must dispose it.
    /// </summary>
    public static IntPtr FromString(string value)
    {
        int result = WindowsCreateString(value, value.Length, out IntPtr hstring);
        Marshal.ThrowExceptionForHR(result);
        return hstring;
    }

    /// <summary>
    /// Copies the contents out of an HSTRING and releases it. Safe to call with zero.
    /// </summary>
    public static string Take(IntPtr hstring)
    {
        if (hstring == 0)
        {
            return "";
        }

        try
        {
            IntPtr buffer = WindowsGetStringRawBuffer(hstring, out int length);
            return buffer == 0 || length <= 0 ? "" : Marshal.PtrToStringUni(buffer, length) ?? "";
        }
        finally
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    /// <summary>Releases an HSTRING. Safe to call with zero.</summary>
    public static void Delete(IntPtr hstring)
    {
        if (hstring != 0)
        {
            _ = WindowsDeleteString(hstring);
        }
    }

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", EntryPoint = "WindowsCreateString",
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr result);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", EntryPoint = "WindowsDeleteString",
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", EntryPoint = "WindowsGetStringRawBuffer",
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);
}