using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OverlayUtils.Core.Diagnostics;
using OverlayUtils.Core.Legacy;

namespace OverlayUtils.Core.Platform;

/// <summary>Exit codes of the elevated helper; the caller turns them into messages.</summary>
public enum HelperResult
{
    Ok = 0,
    Failed = 1,
    NotSigned = 2,
    UntrustedSigner = 3,
    CopyFailed = 4,
    WrongLocation = 5,
}

/// <summary>
/// The admin-only half of switching between modes. Runs inside a short-lived
/// elevated copy of OverlayUtils.exe ("--helper ..."), never in the tray app.
///
/// Enable:  trust the certificate that signed OverlayUtils.UIAccess.exe (only
///          if Windows does not trust it already), copy the app folder to
///          Program Files, remove the old AppUtili install.
/// Disable: remove the certificate we added and the Program Files folder.
///
/// Per-user steps (start-up entry, launching the other exe) stay in the
/// unelevated app, so they apply to the right user account.
/// </summary>
public static class FullModeInstaller
{
    private const string StateFile = "full-mode.json";
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    private sealed record State(string? ImportedThumbprint);

    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppPaths.FullModeInstallDir, AppPaths.UIAccessExeName));

    public static HelperResult Enable(string sourceDir)
    {
        try
        {
            var exe = Path.Combine(sourceDir, AppPaths.UIAccessExeName);
            if (!File.Exists(exe)) return HelperResult.CopyFailed;

            var signer = ReadSigner(exe);
            if (signer is null) return HelperResult.NotSigned;

            string? imported = null;
            using (signer)
            {
                if (!IsTrusted(signer))
                {
                    // Only self-signed code-signing certificates are added; anything
                    // else that Windows does not trust is refused.
                    if (signer.Subject != signer.Issuer || !HasCodeSigningUsage(signer))
                        return HelperResult.UntrustedSigner;
                    AddToStore(signer, StoreName.Root);
                    AddToStore(signer, StoreName.TrustedPublisher);
                    imported = signer.Thumbprint;
                }
            }

            var previous = ReadState();
            if (!CopyInto(sourceDir, AppPaths.FullModeInstallDir)) return HelperResult.CopyFailed;
            WriteState(new State(imported ?? previous?.ImportedThumbprint));

            if (LegacyInstall.IsPresent()) LegacyInstall.Remove();
            return HelperResult.Ok;
        }
        catch (Exception e)
        {
            Log.Error("Enabling full mode", e);
            return HelperResult.Failed;
        }
    }

    public static HelperResult Disable()
    {
        try
        {
            if (IsInside(AppPaths.AppDir, AppPaths.FullModeInstallDir))
                return HelperResult.WrongLocation; // cannot delete the folder we run from

            var state = ReadState();
            if (state?.ImportedThumbprint is { } thumbprint)
            {
                RemoveFromStore(thumbprint, StoreName.Root);
                RemoveFromStore(thumbprint, StoreName.TrustedPublisher);
            }
            if (Directory.Exists(AppPaths.FullModeInstallDir))
                Directory.Delete(AppPaths.FullModeInstallDir, recursive: true);
            return HelperResult.Ok;
        }
        catch (Exception e)
        {
            Log.Error("Disabling full mode", e);
            return HelperResult.Failed;
        }
    }

    internal static X509Certificate2? ReadSigner(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057 // no replacement exists for reading an Authenticode signer
            using var cert = X509Certificate.CreateFromSignedFile(file);
            return new X509Certificate2(cert);
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException)
        {
            return null; // not signed
        }
    }

    private static bool IsTrusted(X509Certificate2 cert)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(CodeSigningOid));
        return chain.Build(cert);
    }

    private static bool HasCodeSigningUsage(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigningOid));

    private static void AddToStore(X509Certificate2 cert, StoreName name)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        using var publicOnly = X509CertificateLoader.LoadCertificate(cert.RawData);
        store.Add(publicOnly);
    }

    private static void RemoveFromStore(string thumbprint, StoreName name)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        foreach (var cert in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            store.Remove(cert);
            cert.Dispose();
        }
    }

    /// <summary>Copies to "&lt;target&gt;.new", then swaps it in, so a failed copy leaves the old install intact.</summary>
    internal static bool CopyInto(string source, string target)
    {
        if (AppPaths.SamePath(source, target)) return true;
        var staging = target + ".new";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            CopyTree(source, staging);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"Copying {source} to {target}", e);
            return false;
        }
    }

    internal static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    internal static bool IsInside(string path, string folder)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    // The state file sits next to the install, so removing the folder removes it too.
    private static string StatePath => Path.Combine(AppPaths.FullModeInstallDir, StateFile);

    private static State? ReadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) : null; }
        catch (Exception) { return null; }
    }

    private static void WriteState(State state) =>
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
}
