using System.Reflection;
using System.Runtime.InteropServices;

namespace sharpclaw.Interop;

/// <summary>
/// 统一管理 Wasmer / Wasmtime 原生库的 DllImportResolver，
/// 避免对同一 Assembly 多次注册 resolver 时抛出 InvalidOperationException。
/// </summary>
internal static class NativeWasmLibraryResolver
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, IntPtr> Handles = new();
    private static bool _registered;

    internal static void Register(string libraryName, IntPtr handle)
    {
        lock (Lock)
        {
            Handles[libraryName] = handle;

            if (_registered)
                return;

            NativeLibrary.SetDllImportResolver(
                typeof(NativeWasmLibraryResolver).Assembly,
                Resolve);
            _registered = true;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        lock (Lock)
        {
            return Handles.TryGetValue(libraryName, out var handle) ? handle : IntPtr.Zero;
        }
    }
}
