using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using DragShift.Core;

namespace DragShift.App;

/// <summary>Presentations → PDF with PowerPoint, driven invisibly through its automation interface.</summary>
sealed class PowerPointConverter : IOfficeConverter
{
    const int PpSaveAsPdf = 32, MsoTrue = -1, MsoFalse = 0;
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>One presentation at a time: there's a single PowerPoint process, possibly the user's own.</summary>
    static readonly object Gate = new();

    readonly Type type;

    PowerPointConverter(Type type) => this.type = type;

    /// <summary>Null when PowerPoint isn't installed. Doesn't start it.</summary>
    public static PowerPointConverter? TryCreate() => Type.GetTypeFromProgID("PowerPoint.Application") is { } t ? new(t) : null;

    public void ToPdf(string source, string pdfPath)
    {
        lock (Gate) Sta.Run(() => Export(Path.GetFullPath(source), Path.GetFullPath(pdfPath)), Timeout, "PowerPoint");
    }

    void Export(string source, string pdf)
    {
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            // Read-only, with a title, without a window.
            dynamic deck = app.Presentations.Open(source, MsoTrue, MsoFalse, MsoFalse);
            try
            {
                deck.SaveAs(pdf, PpSaveAsPdf);
            }
            finally
            {
                deck.Close();
                Marshal.FinalReleaseComObject(deck);
            }
        }
        finally
        {
            try
            {
                // A PowerPoint the user opened is visible and stays; a hidden one with nothing open is ours (or a
                // leftover from an earlier conversion) and is closed.
                if ((int)app.Visible == MsoFalse && (int)app.Presentations.Count == 0) app.Quit();
            }
            catch (COMException)
            {
                // Already gone.
            }
            Marshal.FinalReleaseComObject(app);
        }
    }
}

/// <summary>Presentations → PDF with LibreOffice, for PCs without PowerPoint.</summary>
sealed class LibreOfficeConverter : IOfficeConverter
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>LibreOffice handles one request per profile at a time.</summary>
    static readonly object Gate = new();

    readonly string soffice;

    LibreOfficeConverter(string soffice) => this.soffice = soffice;

    public static LibreOfficeConverter? TryCreate()
    {
        var dirs = new List<string>();
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }.Select(Environment.GetFolderPath))
        {
            if (root.Length > 0) dirs.Add(Path.Combine(root, "LibreOffice", "program"));
        }
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs)
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), "soffice.exe"); }
            catch (ArgumentException) { continue; }
            if (File.Exists(candidate)) return new LibreOfficeConverter(candidate);
        }
        return null;
    }

    public void ToPdf(string source, string pdfPath)
    {
        var outDir = Directory.CreateTempSubdirectory("dragshift-lo-").FullName;
        // Its own profile, so a LibreOffice window the user has open doesn't take the request (and stays untouched).
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DragShift", "LibreOffice");
        try
        {
            var psi = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-env:UserInstallation=" + new Uri(profile).AbsoluteUri);
            foreach (var arg in new[] { "--headless", "--norestore", "--convert-to", "pdf", "--outdir", outDir, Path.GetFullPath(source) })
                psi.ArgumentList.Add(arg);
            lock (Gate)
            {
                using var p = Process.Start(psi) ?? throw new UserFacingException("LibreOffice didn't start.");
                if (!p.WaitForExit(Timeout))
                {
                    try { p.Kill(true); } catch (InvalidOperationException) { /* exited meanwhile */ }
                    throw new UserFacingException("LibreOffice took too long to open the slides.");
                }
            }
            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(source) + ".pdf");
            if (!File.Exists(produced)) throw new UserFacingException($"LibreOffice couldn't open “{Path.GetFileName(source)}”.");
            File.Move(produced, pdfPath, true);
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { /* best effort */ }
        }
    }
}

static class Sta
{
    /// <summary>Runs automation work on its own STA thread, giving up after <paramref name="timeout"/>.</summary>
    public static void Run(Action work, TimeSpan timeout, string what)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception e) { error = e; }
        }) { IsBackground = true, Name = what };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout)) throw new UserFacingException($"{what} took too long to open the slides (is it asking for a password?).");
        if (error is not null) ExceptionDispatchInfo.Throw(error);
    }
}
