using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AppAgasCs.Features.Ai.Generation;

public sealed class AiGeneratedImageStore
{
    internal async Task<AiGenerationResult> SaveAsync(string projectRoot, GeneratedImageData image, CancellationToken cancellationToken)
        => await SaveAsync(
            projectRoot,
            new AiGenerationStorageScope(string.Empty, string.Empty, string.Empty, string.Empty),
            image,
            cancellationToken).ConfigureAwait(false);

    internal async Task<AiGenerationResult> SaveAsync(
        string projectRoot,
        AiGenerationStorageScope storageScope,
        GeneratedImageData image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storageScope);
        ArgumentNullException.ThrowIfNull(image);

        string mimeType = GeneratedImageData.NormalizeMimeType(image.MimeType);
        ValidateImageBytes(image.Bytes, mimeType);
        string extension = mimeType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => throw new AiGenerationException(AiGenerationErrorCode.UnsupportedImage, "The provider returned an unsupported image type.")
        };

        string root;
        try
        {
            root = Path.GetFullPath(projectRoot);
        }
        catch (AiGenerationException)
        {
            throw;
        }
        catch
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image directory could not be prepared.");
        }
        try
        {
            using GeneratedImageFileReservation reservation =
                AgasGeneratedImagePathPolicy.ReserveNextCandidateFile(root, storageScope, extension, asynchronous: true);
            await reservation.Stream.WriteAsync(image.Bytes, cancellationToken).ConfigureAwait(false);
            await reservation.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            reservation.Stream.Flush(flushToDisk: true);
            reservation.Commit();
            string relativePath = AgasGeneratedImagePathPolicy.ToProjectRelativePath(root, reservation.FinalPath);
            return new AiGenerationResult(
                relativePath,
                mimeType,
                image.Bytes.LongLength,
                image.EffectiveOperation,
                image.EditGuarantee,
                image.PixelWidth,
                image.PixelHeight);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiGenerationException)
        {
            throw;
        }
        catch
        {
            throw new AiGenerationException(AiGenerationErrorCode.SaveFailed, "The generated image could not be saved.");
        }
    }

    private static void ValidateImageBytes(byte[] bytes, string mimeType)
    {
        bool valid = mimeType switch
        {
            "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            "image/webp" => bytes.Length >= 12
                && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!valid)
        {
            throw new AiGenerationException(AiGenerationErrorCode.UnsupportedImage, "The provider response is not a valid supported image.");
        }
    }

}
