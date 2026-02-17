namespace TDSQueue.Proxy;

public static class SecretResolver
{
    public static string Resolve(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return ReplaceEnvTokens(value);
    }

    private static string ReplaceEnvTokens(string input)
    {
        var output = input;
        var cursor = 0;

        while (cursor < output.Length)
        {
            var start = output.IndexOf("${ENV:", cursor, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            var end = output.IndexOf('}', start);
            if (end < 0)
            {
                break;
            }

            var variableName = output[(start + 6)..end];
            var resolved = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new InvalidOperationException($"Environment variable '{variableName}' is not set.");
            }

            output = string.Concat(output.AsSpan(0, start), resolved, output.AsSpan(end + 1));
            cursor = start + resolved.Length;
        }

        return output;
    }
}
