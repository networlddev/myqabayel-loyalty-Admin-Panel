using Microsoft.AspNetCore.Mvc;

namespace MyQabayel.Admin.Services;

public sealed class PromoImageStorage(IWebHostEnvironment hosting,
    PublicAssetUrlService assetUrls)
{
    public async Task<string> SaveAsync(IFormFile image, HttpRequest request,
        CancellationToken token)
    {
        if (image.Length is < 1 or > 5 * 1024 * 1024)
            throw new InvalidOperationException("The image must be no larger than 5 MB.");
        byte[] signature = new byte[12];
        await using (var input = image.OpenReadStream())
        {
            int read = 0;
            while (read < signature.Length)
            {
                int count = await input.ReadAsync(signature.AsMemory(read), token);
                if (count == 0) break;
                read += count;
            }
        }
        string extension = signature.AsSpan(0, 3).SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF })
            ? ".jpg"
            : signature.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                ? ".png"
                : signature.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                  && signature.AsSpan(8, 4).SequenceEqual("WEBP"u8)
                    ? ".webp" : "";
        if (extension.Length == 0)
            throw new InvalidOperationException("Upload a valid JPEG, PNG or WebP image.");
        string folder = Path.Combine(hosting.WebRootPath, "uploads", "promo");
        Directory.CreateDirectory(folder);
        string name = $"{Guid.NewGuid():N}{extension}";
        await using (var output = File.Create(Path.Combine(folder, name)))
            await image.CopyToAsync(output, token);
        return assetUrls.ToAbsolute($"/uploads/promo/{name}", request);
    }
}
