namespace RockBot.UserProxy.Cli;

/// <summary>
/// Guesses the MIME type of a file the user passed to <c>--attach</c> from its extension. Only
/// the types the agent accepts are listed; anything else resolves to
/// <c>application/octet-stream</c> and is refused by the agent with a message naming what it
/// does accept, which is a better answer than the CLI inventing one.
/// </summary>
internal static class AttachmentMimeGuess
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".pdf"] = "application/pdf",
    };

    public static string FromFileName(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName), out var mime)
            ? mime
            : "application/octet-stream";
}
