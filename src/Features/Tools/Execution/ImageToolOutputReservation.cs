using System;
using System.Globalization;
using System.IO;

namespace AppAgasCs.Features.Tools.Execution;

/// <summary>Reserves a project-wide tool output number until the secure PNG writer finishes.</summary>
internal sealed class ImageToolOutputReservation : IDisposable
{
    private const string Prefix = "Tools ";
    private const string ReservationSuffix = ".reserve";
    private readonly FileStream _reservation;

    private ImageToolOutputReservation(string relativePath, FileStream reservation)
    {
        RelativePath = relativePath;
        _reservation = reservation;
    }

    public string RelativePath { get; }

    public static ImageToolOutputReservation Reserve(string projectRoot)
    {
        string root = ImageToolProjectPaths.ValidateRoot(projectRoot);
        // Validate the entire directory chain before enumeration or reservation creation.
        ImageToolProjectPaths.NewPng(root, $"agas/tools/{Guid.NewGuid():N}.png");
        string directory = Path.Combine(root, "agas", "tools");
        int maximum = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            if (TryNumber(Path.GetFileName(path), out int value)) maximum = Math.Max(maximum, value);

        while (maximum < int.MaxValue)
        {
            string name = Prefix + (++maximum).ToString(CultureInfo.InvariantCulture);
            string relativePath = $"agas/tools/{name}.png";
            string reservationPath = Path.Combine(directory, $".{name}{ReservationSuffix}");
            FileStream reservation;
            try
            {
                ImageToolProjectPaths.NewPng(root, relativePath);
                reservation = new FileStream(reservationPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (ImageToolRunException error) when (error.Code == ImageToolRunErrorCodes.OutputExists) { continue; }
            catch (IOException) when (Path.Exists(reservationPath)) { continue; }

            try
            {
                // Another writer may have committed and released its reservation since our scan.
                ImageToolProjectPaths.NewPng(root, relativePath);
                return new ImageToolOutputReservation(relativePath, reservation);
            }
            catch (ImageToolRunException error) when (error.Code == ImageToolRunErrorCodes.OutputExists)
            {
                reservation.Dispose();
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }
        throw new IOException("Tool output number is exhausted.");
    }

    private static bool TryNumber(string fileName, out int value)
    {
        value = 0;
        string stem;
        if (fileName.StartsWith('.') && fileName.EndsWith(ReservationSuffix, StringComparison.OrdinalIgnoreCase))
            stem = fileName[1..^ReservationSuffix.Length];
        else
        {
            int extension = fileName.IndexOf('.');
            stem = extension < 0 ? fileName : fileName[..extension];
        }
        return stem.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(stem.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;
    }

    public void Dispose() => _reservation.Dispose();
}
