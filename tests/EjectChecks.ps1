param([string]$AssemblyPath = (Join-Path $PSScriptRoot '../bin/Debug/net8.0-windows/VibeCopy.dll'))

# Read-only checks: no removable drive is passed to EjectAsync and no eject verb is invoked.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Resolve-Path $AssemblyPath)
Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
public static class ExplorerEjectChecks {
    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void LoadTypeLibEx(string path, int kind, out ITypeLib library);

    public static int Run(Assembly assembly, string root) {
        int checks = 0;
        Exception error = null;
        var thread = new Thread(() => {
            object shell = null, computer = null, item = null;
            ITypeLib library = null;
            try {
                var owner = assembly.GetType("VibeCopy.Shell", true);
                // Compare our dispatch IDs to Windows metadata without executing the verbs.
                LoadTypeLibEx(Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\shell32.dll", 2, out library);
                foreach (var interfaceName in new[] { "IShellDispatch", "IShellFolder", "IShellItem" }) {
                    var type = owner.GetNestedType(interfaceName, BindingFlags.NonPublic);
                    var guid = type.GUID;
                    library.GetTypeInfoOfGuid(ref guid, out var info);
                    try {
                        foreach (var method in type.GetMethods()) {
                            int[] ids = new int[1];
                            info.GetIDsOfNames(new[] { method.Name }, 1, ids);
                            var expected = method.GetCustomAttribute<DispIdAttribute>().Value;
                            if (ids[0] != expected) throw new Exception("Wrong dispatch ID: " + method.Name);
                            checks++;
                        }
                    } finally { Marshal.FinalReleaseComObject(info); }
                }
                // Use the actual application's COM declarations to look up a fixed drive.
                shell = Activator.CreateInstance(owner.GetNestedType("ShellApplication", BindingFlags.NonPublic));
                computer = owner.GetNestedType("IShellDispatch", BindingFlags.NonPublic)
                    .GetMethod("NameSpace").Invoke(shell, new object[] { 17 });
                if (computer == null) throw new Exception("This PC lookup failed");
                checks++;
                item = owner.GetNestedType("IShellFolder", BindingFlags.NonPublic)
                    .GetMethod("ParseName").Invoke(computer, new object[] { root });
                var itemType = owner.GetNestedType("IShellItem", BindingFlags.NonPublic);
                if (item == null || !itemType.IsInstanceOfType(item)) throw new Exception("Drive lookup failed");
                checks++;
            } catch (Exception ex) { error = ex; }
            finally {
                if (item != null) Marshal.FinalReleaseComObject(item);
                if (computer != null) Marshal.FinalReleaseComObject(computer);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
                if (library != null) Marshal.FinalReleaseComObject(library);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new Exception("Explorer lookup timed out");
        if (error != null) throw error;
        return checks;
    }
}
'@

$checks = 0
foreach ($invalid in @('', 'C', 'C:\folder', '\\server\share', 'C:\..\', 'C:\\', '1:\')) {
    $result = [VibeCopy.Shell]::EjectAsync($invalid).GetAwaiter().GetResult()
    if ($result.Item1 -or -not $result.Item2.Contains('根目录')) { throw "Invalid drive path accepted: $invalid" }
    $checks++
}
$fixed = [IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq [IO.DriveType]::Fixed -and $_.IsReady } | Select-Object -First 1
if ($null -eq $fixed) { throw 'No fixed drive available for read-only checks' }
$result = [VibeCopy.Shell]::EjectAsync($fixed.Name).GetAwaiter().GetResult()
if ($result.Item1 -or $result.Item2 -ne '仅支持弹出可移动盘。') { throw 'Fixed drive protection failed' }
$checks++
$checks += [ExplorerEjectChecks]::Run([VibeCopy.Shell].Assembly, $fixed.Name)
Write-Output "PASS: $checks eject checks (read-only; no hardware ejection)"
