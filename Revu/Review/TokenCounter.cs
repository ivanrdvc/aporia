using Microsoft.ML.Tokenizers;

namespace Revu.Review;

public static class TokenCounter
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static int Count(string text) => Tokenizer.CountTokens(text);
}
