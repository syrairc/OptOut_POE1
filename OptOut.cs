using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ExileCore;
using ExileCore.Shared;
using ExileCore.Shared.Interfaces;
using ImGuiNET;

namespace OptOut;

internal static class Native
{
    public const uint VmOperation = 0x0008;
    public const uint VmRead = 0x0010;
    public const uint VmWrite = 0x0020;
    public const uint QueryLimitedInformation = 0x1000;
    public const uint TokenQuery = 0x0008;
    public const uint ExecuteReadWrite = 0x40;

    public static byte[] Read(IntPtr handle, long address, int size)
    {
        var buffer = new byte[size];
        return ReadProcessMemory(handle, new IntPtr(address), buffer, size, out var got) && got.ToInt64() == size
            ? buffer
            : null;
    }

    // gets a handle to the game process, the access rights we ask for are in the call below
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    // hands a process or token handle back as soon as we are done with it
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    // reads the game's own code, to find the crash reporter and to check the bytes are what we expect
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    // writes the single ret byte over the crash reporter entry, and puts the original back
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr wrote);

    // makes that code page writable for the moment the write takes, then restores the old protection
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint protect, out uint old);

    // tells the cpu the code changed, or it can keep running the bytes it already cached
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    // the game's user token, needed because the game can run as a different windows user than the HUD
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    // asks windows for that user's Documents path, which handles OneDrive redirection
    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    // frees the path string SHGetKnownFolderPath allocated for us
    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);
}

public class OptOut : BaseSettingsPlugin<OptOutSettings>
{
    public const string BridgeIsBlocked = "OptOut.IsCrashReportBlocked";
    public const string BridgeStatus = "OptOut.StatusText";

    private CrashReportPatch patch;
    private bool patchFailed;
    private bool prefilled;
    private int patchPid;

    public override bool Initialise()
    {
        Name = "Opt Out";
        GameController.PluginBridge.SaveMethod(BridgeIsBlocked, (Func<bool>)(() => patch is { IsPatched: true }));
        GameController.PluginBridge.SaveMethod(BridgeStatus, (Func<string>)StatusText);

        Settings.Unpatch.OnPressed += () =>
        {
            Settings.PatchReporter.Value = false;
            patch?.Restore();
        };

        Settings.LockFolder.OnPressed += () => WithFolder(dir => DumpFolderLock.Apply(dir, Settings.BlockFolderReads.Value));
        Settings.UnlockFolder.OnPressed += () => WithFolder(DumpFolderLock.Revert);

        return true;
    }

    // the [Menu] nodes stay the single source of truth, we just draw them by name in two sections
    // with a divider and a line at the bottom of each saying what it does. anything missing falls
    // back to the plain flat list so a control can never vanish
    public override void DrawSettings()
    {
        var byName = new Dictionary<string, ISettingsHolder>();
        foreach (var h in Drawers)
            if (h?.Name != null) byName.TryAdd(h.Name, h);

        string[] needed =
        {
            "Patch the crash reporter", "Unpatch now",
            "Game folder", "Block folder reads", "Lock the crash folder", "Unlock the crash folder"
        };
        foreach (var n in needed)
            if (!byName.ContainsKey(n)) { base.DrawSettings(); return; }

        try
        {
            void Draw(string name) => byName[name].Draw();

            if (byName.TryGetValue("Enable", out var enable)) enable.Draw();
            else
            {
                var on = Settings.Enable.Value;
                if (ImGui.Checkbox("Enable", ref on)) Settings.Enable.Value = on;
            }

            ImGui.SeparatorText("Memory patch");
            Draw("Patch the crash reporter");
            Draw("Unpatch now");
            Note("One 0xC3 written over the first instruction of the crash reporter, so a crash returns "
               + "before it writes or sends anything. It is a memory write, so it dies with the client "
               + "and goes back on every launch. Off until you turn it on, since it touches the game.");

            ImGui.SeparatorText("Crash report folder");
            Draw("Game folder");
            Draw("Block folder reads");
            Draw("Lock the crash folder");
            Draw("Unlock the crash folder");
            Note("Denies the folder permission to create files, so the reporter's writes fail even with "
               + "the patch off. It is an ACL on disk, not a memory write, so it outlives the client and "
               + "also stops the leftover upload at the next launch. Permanent until you Unlock, even "
               + "after the plugin is gone.");
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.Message);
            base.DrawSettings();
        }
    }

    private static void Note(string text)
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, Grey);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private static readonly Vector4 Grey = new(0.65f, 0.65f, 0.65f, 1f);

    public override Job Tick()
    {
        var process = GameController?.Memory?.Process;
        if (process == null) return null;

        if (patchPid != process.Id)
        {
            Shutdown();
            patchPid = process.Id;
        }

        if (!prefilled)
        {
            prefilled = true;
            if (string.IsNullOrWhiteSpace(Settings.GameFolder.Value))
            {
                var games = DumpFolderLock.Derive(process);
                if (games != null) Settings.GameFolder.Value = games;
            }
        }

        if (!Settings.PatchReporter.Value)
        {
            patch?.Dispose();
            patch = null;
            patchFailed = false;
            return null;
        }

        if (patch == null)
        {
            if (!patchFailed)
            {
                patch = CrashReportPatch.Attach(patchPid, GameController.Memory.AddressOfProcess);
                patchFailed = patch == null;
            }

            return null;
        }

        if (!patch.Ensure())
        {
            patch.Dispose();
            patch = null;
            patchFailed = true;
        }

        return null;
    }

    public override void OnClose() => Shutdown();

    public override void OnPluginDestroyForHotReload() => Shutdown();

    private void WithFolder(Action<string> action)
    {
        var dir = DumpFolderLock.Resolve(GameController?.Memory?.Process, Settings.GameFolder.Value);
        if (dir != null) action(dir);
    }

    private string StatusText()
    {
        if (!Settings.PatchReporter.Value) return "off";
        if (patch == null) return patchFailed ? "failed" : "starting";
        return patch.IsPatched ? "blocked" : "not blocked";
    }

    private void Shutdown()
    {
        patch?.Dispose();
        patch = null;
        patchFailed = false;
        prefilled = false;
    }
}

internal sealed class CrashReportPatch : IDisposable
{
    private const string Anchor = "PoE-%ld-%ld.dmp";

    private static readonly byte[] Prologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x44, 0x88, 0x4C, 0x24, 0x20, 0x48, 0x89, 0x54, 0x24, 0x10 };

    private static readonly byte[] Patched = BuildPatched();

    private readonly IntPtr handle;
    private readonly IntPtr address;

    public bool IsPatched { get; private set; }
    public bool AppliedByUs { get; private set; }

    private CrashReportPatch(IntPtr handle, IntPtr address, bool isPatched)
    {
        this.handle = handle;
        this.address = address;
        IsPatched = isPatched;
    }

    private static byte[] BuildPatched()
    {
        var bytes = (byte[])Prologue.Clone();
        bytes[0] = 0xC3;
        return bytes;
    }

    public static CrashReportPatch Attach(int pid, long moduleBase)
    {
        if (moduleBase == 0)
        {
            DebugWindow.LogError("no client module base");
            return null;
        }

        var handle = Native.OpenProcess(Native.VmOperation | Native.VmRead | Native.VmWrite, false, pid);
        if (handle == IntPtr.Zero)
        {
            DebugWindow.LogError("cannot open the client, run the HUD as admin");
            return null;
        }

        var rva = ClientImage.ResolveAnchor(handle, moduleBase, Anchor);
        if (rva == 0)
        {
            Native.CloseHandle(handle);
            return null;
        }

        var address = moduleBase + rva;
        var live = Native.Read(handle, address, Prologue.Length);

        if (Same(live, Prologue)) return new CrashReportPatch(handle, new IntPtr(address), false);
        if (Same(live, Patched)) return new CrashReportPatch(handle, new IntPtr(address), true);

        DebugWindow.LogError(live == null
            ? "cannot read the reporter entry"
            : "prologue mismatch, got " + BitConverter.ToString(live));
        Native.CloseHandle(handle);
        return null;
    }

    public bool Ensure()
    {
        var live = Native.Read(handle, address.ToInt64(), Prologue.Length);
        if (live == null) return true;

        if (Same(live, Patched))
        {
            IsPatched = true;
            return true;
        }

        if (!Same(live, Prologue))
        {
            DebugWindow.LogError("the reporter entry changed under us, leaving it alone");
            return false;
        }

        if (IsPatched) DebugWindow.LogMsg("the patch was reverted, putting it back");

        IsPatched = Write(Patched);
        AppliedByUs = IsPatched;
        return true;
    }

    public void Restore()
    {
        if (!IsPatched) return;

        if (!AppliedByUs)
        {
            DebugWindow.LogMsg("something else patched this, leaving it alone");
            return;
        }

        if (!Same(Native.Read(handle, address.ToInt64(), Prologue.Length), Patched))
        {
            DebugWindow.LogError("the reporter entry changed under us, leaving it alone");
            return;
        }

        if (!Write(Prologue)) return;

        IsPatched = false;
        AppliedByUs = false;
        DebugWindow.LogMsg("patch removed");
    }

    public void Dispose()
    {
        if (handle != IntPtr.Zero) Native.CloseHandle(handle);
    }

    private bool Write(byte[] bytes)
    {
        var size = new UIntPtr((uint)bytes.Length);
        if (!Native.VirtualProtectEx(handle, address, size, Native.ExecuteReadWrite, out var old))
        {
            DebugWindow.LogError("VirtualProtectEx failed");
            return false;
        }

        var ok = Native.WriteProcessMemory(handle, address, bytes, bytes.Length, out var wrote)
                 && wrote.ToInt64() == bytes.Length;

        Native.VirtualProtectEx(handle, address, size, old, out _);
        Native.FlushInstructionCache(handle, address, size);

        if (!ok) DebugWindow.LogError("write failed, run the HUD as admin");
        return ok;
    }

    private static bool Same(byte[] a, byte[] b) => a != null && b != null && a.AsSpan().SequenceEqual(b);
}

internal static class ClientImage
{
    public static long ResolveAnchor(IntPtr handle, long moduleBase, string anchor)
    {
        var hdr = Native.Read(handle, moduleBase, 0x1000);
        if (hdr == null) { DebugWindow.LogError("cannot read the PE headers"); return 0; }

        var peOff = BitConverter.ToInt32(hdr, 0x3C);
        if (peOff <= 0 || peOff > 0xF00 || BitConverter.ToUInt32(hdr, peOff) != 0x00004550)
        { DebugWindow.LogError("bad PE signature"); return 0; }

        var coff = peOff + 4;
        int nsec = BitConverter.ToUInt16(hdr, coff + 2);
        int optsz = BitConverter.ToUInt16(hdr, coff + 16);
        var opt = coff + 20;
        if (BitConverter.ToUInt16(hdr, opt) != 0x20B) { DebugWindow.LogError("not a PE32+ image"); return 0; }

        var excDir = opt + 112 + 3 * 8;
        var pdataRva = BitConverter.ToUInt32(hdr, excDir);
        var pdataSize = (int)BitConverter.ToUInt32(hdr, excDir + 4);
        if (pdataRva == 0 || pdataSize < 12) { DebugWindow.LogError("no exception directory"); return 0; }

        var sections = new List<(uint Rva, int Size, bool Exec)>();
        var sh = opt + optsz;
        for (var i = 0; i < nsec; i++)
        {
            var o = sh + i * 40;
            if (o + 40 > hdr.Length) break;
            var vsize = (int)BitConverter.ToUInt32(hdr, o + 8);
            var vaddr = BitConverter.ToUInt32(hdr, o + 12);
            var chars = BitConverter.ToUInt32(hdr, o + 36);
            if (vsize > 0) sections.Add((vaddr, vsize, (chars & 0x20000000) != 0));
        }

        var text = sections.Find(s => s.Exec);
        if (text.Size == 0) { DebugWindow.LogError("no executable section"); return 0; }

        var needle = Encoding.Unicode.GetBytes(anchor);
        var strRvas = new List<long>();
        foreach (var s in sections)
        {
            if (s.Exec) continue;
            var buf = Native.Read(handle, moduleBase + s.Rva, s.Size);
            if (buf == null) continue;

            for (var at = 0; at < buf.Length;)
            {
                var hit = buf.AsSpan(at).IndexOf(needle);
                if (hit < 0) break;
                at += hit;
                strRvas.Add(s.Rva + at);
                at++;
            }
        }
        if (strRvas.Count == 0) { DebugWindow.LogError("anchor not found: " + anchor); return 0; }

        var code = Native.Read(handle, moduleBase + text.Rva, text.Size);
        if (code == null) { DebugWindow.LogError("cannot read the code section"); return 0; }

        long leaRva = 0;
        var leaHits = 0;
        for (var k = 0; k + 7 <= code.Length; k++)
        {
            if (code[k] != 0x48 && code[k] != 0x4C) continue;
            if (code[k + 1] != 0x8D) continue;
            if ((code[k + 2] & 0xC7) != 0x05) continue;
            var disp = BitConverter.ToInt32(code, k + 3);
            if (!strRvas.Contains(text.Rva + k + 7 + disp)) continue;
            leaHits++;
            leaRva = text.Rva + k;
        }
        if (leaHits != 1) { DebugWindow.LogError($"anchor has {leaHits} lea sites, wanted 1"); return 0; }

        var pdata = Native.Read(handle, moduleBase + pdataRva, pdataSize);
        if (pdata == null) { DebugWindow.LogError("cannot read .pdata"); return 0; }

        var lo = 0;
        var hi = pdataSize / 12 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var begin = BitConverter.ToUInt32(pdata, mid * 12);
            var end = BitConverter.ToUInt32(pdata, mid * 12 + 4);
            if (leaRva < begin) hi = mid - 1;
            else if (leaRva >= end) lo = mid + 1;
            else return begin;
        }

        DebugWindow.LogError("no .pdata entry holds the lea");
        return 0;
    }
}

internal static class DumpFolderLock
{
    private const string GameFolder = "Path of Exile";
    private const string DumpFolder = "release";

    // FOLDERID_Documents from KnownFolders.h, the id SHGetKnownFolderPath wants
    private static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    // S-1-1-0. denying Everyone binds whichever windows user runs the client, elevated or not
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    public static string Derive(Process process)
    {
        var docs = DocumentsOf(process);
        if (docs == null) return null;

        var games = Path.Combine(docs, "My Games", GameFolder);
        return Valid(games) ? games : null;
    }

    public static string Resolve(Process process, string gameFolderOverride)
    {
        string games;
        if (string.IsNullOrWhiteSpace(gameFolderOverride))
        {
            games = Derive(process);
        }
        else
        {
            games = gameFolderOverride.Trim();
            if (!Valid(games)) return null;
        }

        return games == null ? null : Path.Combine(games, DumpFolder);
    }

    private static bool Valid(string games)
    {
        try
        {
            if (!Directory.Exists(games))
            {
                DebugWindow.LogError("no such folder, " + games);
                return false;
            }

            var leaf = games.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetFileName(leaf), GameFolder, StringComparison.OrdinalIgnoreCase))
            {
                DebugWindow.LogError($"not a {GameFolder} folder, " + games);
                return false;
            }

            if (Directory.GetFiles(games, "*_Config.ini").Length == 0)
            {
                DebugWindow.LogError("no client config in " + games + ", so that is not a game folder");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.Message);
            return false;
        }
    }

    public static void Apply(string dir, bool blockReads)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var info = new DirectoryInfo(dir);
            var sec = info.GetAccessControl(AccessControlSections.Access);
            sec.RemoveAccessRule(Rule(true));
            sec.AddAccessRule(Rule(blockReads));
            info.SetAccessControl(sec);
            DebugWindow.LogMsg("locked " + dir);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError("cannot lock the folder, run the HUD as admin. " + ex.Message);
        }
    }

    public static void Revert(string dir)
    {
        if (!Directory.Exists(dir)) return;

        try
        {
            var info = new DirectoryInfo(dir);
            var sec = info.GetAccessControl(AccessControlSections.Access);
            sec.RemoveAccessRule(Rule(true));
            info.SetAccessControl(sec);
            DebugWindow.LogMsg("unlocked " + dir);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError("cannot unlock the folder, " + ex.Message);
        }
    }

    private static FileSystemAccessRule Rule(bool blockReads)
    {
        var rights = FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories;
        if (blockReads) rights |= FileSystemRights.ListDirectory;
        return new FileSystemAccessRule(Everyone, rights, InheritanceFlags.None,
                                        PropagationFlags.None, AccessControlType.Deny);
    }

    private static string DocumentsOf(Process process)
    {
        if (process == null) return null;

        var handle = Native.OpenProcess(Native.QueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            DebugWindow.LogError("cannot open the client, set the game folder by hand");
            return null;
        }

        try
        {
            if (!Native.OpenProcessToken(handle, Native.TokenQuery, out var token))
            {
                DebugWindow.LogError("cannot read the client token, set the game folder by hand");
                return null;
            }

            try
            {
                if (Native.SHGetKnownFolderPath(Documents, 0, token, out var buffer) != 0)
                {
                    DebugWindow.LogError("cannot find the client Documents folder");
                    return null;
                }

                try { return Marshal.PtrToStringUni(buffer); }
                finally { Native.CoTaskMemFree(buffer); }
            }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(handle); }
    }
}
