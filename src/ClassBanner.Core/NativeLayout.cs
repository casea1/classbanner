using System.Runtime.InteropServices;

namespace ClassBanner.Core;

/// <summary>
/// TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES, exactly as Windows lays it out: the LUID is two DWORDs at offset 4,
/// not an 8-byte-aligned long (issue #24: that put it at offset 8, so no privilege was ever enabled). Kept here so a
/// unit test pins the layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct TokenPrivilegesOne
{
    public uint PrivilegeCount;
    public uint LuidLowPart;
    public int LuidHighPart;
    public uint Attributes;

    public static TokenPrivilegesOne Enable(long luid) => new()
    {
        PrivilegeCount = 1,
        LuidLowPart = unchecked((uint)luid),
        LuidHighPart = (int)(luid >> 32),
        Attributes = 0x2, // SE_PRIVILEGE_ENABLED
    };
}
