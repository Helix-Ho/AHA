using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aha.Setting;

/// <summary>SecItem API only: no subprocess, command-line secret, or plaintext fallback.</summary>
internal static class MacKeychain
{
    private const string Service = "AHA.VWorld";
    private const string Account = "api-key";
    private const int NotFound = -25300;
    private const int Duplicate = -25299;

    internal static string Read()
    {
        EnsureSupported();
        using var service = Native.String(Service);
        using var account = Native.String(Account);
        using var query = Native.Dictionary(Query(service, account).Concat(new[]
        {
            (Native.SecurityConstant("kSecReturnData"), Native.CoreConstant("kCFBooleanTrue")),
            (Native.SecurityConstant("kSecMatchLimit"), Native.SecurityConstant("kSecMatchLimitOne"))
        }));
        int status = Native.SecItemCopyMatching(query.Pointer, out var result);
        using var data = new CfValue(result);
        if (status == NotFound) return "";
        Check(status);
        if (result == IntPtr.Zero || Native.CFGetTypeID(result) != Native.CFDataGetTypeID())
            throw new SecurityException("보안 저장소가 올바른 키 데이터를 반환하지 않았습니다.");
        nint length = Native.CFDataGetLength(result);
        if (length is < 0 or > 16384) throw new SecurityException("보안 저장소의 키 크기가 올바르지 않습니다.");
        var bytes = new byte[(int)length];
        try
        {
            if (bytes.Length > 0) Marshal.Copy(Native.CFDataGetBytePtr(result), bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static bool Contains()
    {
        EnsureSupported();
        using var service = Native.String(Service);
        using var account = Native.String(Account);
        // Without kSecReturnData this checks existence without reading the password into managed memory.
        using var query = Native.Dictionary(Query(service, account));
        int status = Native.SecItemCopyMatching(query.Pointer, out var result);
        using var unused = new CfValue(result);
        if (status == NotFound) return false;
        Check(status);
        return true;
    }

    internal static void Write(string value)
    {
        EnsureSupported();
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            if (bytes.Length > 16384) throw new ArgumentException("VWorld KEY가 너무 깁니다.");
            using var service = Native.String(Service);
            using var account = Native.String(Account);
            using var data = Native.Data(bytes);
            var identity = Query(service, account);
            using var query = Native.Dictionary(identity);
            var dataEntry = (Native.SecurityConstant("kSecValueData"), data.Pointer);
            using var update = Native.Dictionary(new[] { dataEntry });
            int status = Native.SecItemUpdate(query.Pointer, update.Pointer);
            if (status == NotFound)
            {
                using var add = Native.Dictionary(identity.Append(dataEntry));
                status = Native.SecItemAdd(add.Pointer, IntPtr.Zero);
                if (status == Duplicate) status = Native.SecItemUpdate(query.Pointer, update.Pointer);
            }
            Check(status);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static void Delete()
    {
        EnsureSupported();
        using var service = Native.String(Service);
        using var account = Native.String(Account);
        using var query = Native.Dictionary(Query(service, account));
        int status = Native.SecItemDelete(query.Pointer);
        if (status != NotFound) Check(status);
    }

    private static (IntPtr, IntPtr)[] Query(CfValue service, CfValue account) => new[]
    {
        (Native.SecurityConstant("kSecClass"), Native.SecurityConstant("kSecClassGenericPassword")),
        (Native.SecurityConstant("kSecAttrService"), service.Pointer),
        (Native.SecurityConstant("kSecAttrAccount"), account.Pointer)
    };

    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsMacOS())
            throw new NotSupportedException("현재 VWorld KEY 보안 저장은 macOS Keychain만 지원합니다.");
    }

    private static void Check(int status)
    {
        if (status == 0) return;
        throw new SecurityException(status switch
        {
            -128 => "키 보안 저장소 접근이 취소되었습니다.",
            -25293 or -25308 => "Keychain 접근을 허용하고 잠금 상태를 확인하세요.",
            _ => $"키 보안 저장소 작업에 실패했습니다. (상태 {status})"
        });
    }

    private sealed class CfValue : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal CfValue(IntPtr pointer) : base(true) { SetHandle(pointer); }
        internal IntPtr Pointer => DangerousGetHandle();
        protected override bool ReleaseHandle() { Native.CFRelease(handle); return true; }
    }

    private static class Native
    {
        private const string SecurityPath = "/System/Library/Frameworks/Security.framework/Security";
        private const string CorePath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly IntPtr Security = NativeLibrary.Load(SecurityPath);
        private static readonly IntPtr Core = NativeLibrary.Load(CorePath);

        internal static IntPtr SecurityConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Security, name));
        internal static IntPtr CoreConstant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Core, name));

        internal static CfValue String(string value) => Owned(CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100));
        internal static CfValue Data(byte[] value) => Owned(CFDataCreate(IntPtr.Zero, value, value.Length));

        internal static CfValue Dictionary(IEnumerable<(IntPtr Key, IntPtr Value)> entries)
        {
            var pairs = entries.ToArray();
            return Owned(CFDictionaryCreate(IntPtr.Zero, pairs.Select(p => p.Key).ToArray(), pairs.Select(p => p.Value).ToArray(),
                pairs.Length, NativeLibrary.GetExport(Core, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(Core, "kCFTypeDictionaryValueCallBacks")));
        }

        private static CfValue Owned(IntPtr value) => value != IntPtr.Zero
            ? new CfValue(value) : throw new SecurityException("키 보안 저장소 요청을 만들지 못했습니다.");

        [DllImport(SecurityPath)] internal static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(SecurityPath)] internal static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(SecurityPath)] internal static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
        [DllImport(SecurityPath)] internal static extern int SecItemDelete(IntPtr query);
        [DllImport(CorePath)] internal static extern void CFRelease(IntPtr value);
        [DllImport(CorePath)] internal static extern nuint CFGetTypeID(IntPtr value);
        [DllImport(CorePath)] internal static extern nuint CFDataGetTypeID();
        [DllImport(CorePath)] internal static extern nint CFDataGetLength(IntPtr data);
        [DllImport(CorePath)] internal static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CorePath)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(CorePath)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
        [DllImport(CorePath)] private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values,
            nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
    }
}
