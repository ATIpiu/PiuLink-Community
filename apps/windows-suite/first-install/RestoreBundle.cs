using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

internal static class RestoreBundle
{
    private static string Digest(string path)
    {
        using (var input = File.OpenRead(path))
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    private static void RestoreCabinet(string root, string name, string expected)
    {
        var rows = File.ReadAllLines(Path.Combine(root, name + ".tsv"));
        var output = Path.Combine(root, name + ".cab");
        var start = new ProcessStartInfo(Path.Combine(root, "wixnative.exe"),
            "smartcab " + name + ".cab 3 " + rows.Length + " 0 0");
        start.WorkingDirectory = root;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        using (var process = Process.Start(start))
        {
            using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                writer.WriteLine(":");
                foreach (var row in rows)
                {
                    var entry = row.Split('\t');
                    writer.WriteLine(Path.Combine(root, "files", entry[0]) + "\t" + entry[1]);
                }
                writer.WriteLine();
            }
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Unable to rebuild " + name);
        }
        // CAB file dates and attributes depend on the extracting filesystem.
        // Preserve the original directory bytes, then check every compressed byte.
        var header = File.ReadAllBytes(Path.Combine(root, name + ".header"));
        using (var file = File.OpenWrite(output))
            file.Write(header, 0, header.Length);
        if (Digest(output) != expected) throw new InvalidDataException("Cabinet verification failed: " + name);
    }

    private static void Assemble(string root, string output)
    {
        var inputs = new Dictionary<string, FileStream>();
        var buffer = new byte[64 * 1024];
        try
        {
            using (var result = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            foreach (var row in File.ReadLines(Path.Combine(root, "layout.tsv")))
            {
                var fields = row.Split('\t');
                FileStream input;
                if (!inputs.TryGetValue(fields[0], out input))
                {
                    input = File.OpenRead(Path.Combine(root, fields[0]));
                    inputs.Add(fields[0], input);
                }
                input.Position = long.Parse(fields[1]);
                var remaining = int.Parse(fields[2]);
                while (remaining > 0)
                {
                    var read = input.Read(buffer, 0, Math.Min(remaining, buffer.Length));
                    if (read == 0) throw new EndOfStreamException();
                    result.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
        }
        finally { foreach (var input in inputs.Values) input.Dispose(); }
    }

    private static int Main(string[] args)
    {
        string temporary = null;
        try
        {
            if (args.Length != 1) return 2;
            var root = AppDomain.CurrentDomain.BaseDirectory;
            var target = Path.GetFullPath(args[0]);
            var hash = File.ReadAllText(Path.Combine(root, "sha256.txt")).Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target) && Digest(target) == hash) return 0;
            foreach (var line in File.ReadLines(Path.Combine(root, "cabinets.tsv")))
            {
                var cabinet = line.Split('\t');
                RestoreCabinet(root, cabinet[0], cabinet[1]);
            }
            temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Assemble(root, temporary);
            if (Digest(temporary) != hash) throw new InvalidDataException("Installer SHA-256 mismatch.");
            try
            {
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            catch (IOException) { if (!File.Exists(target) || Digest(target) != hash) throw; }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
    }
}
