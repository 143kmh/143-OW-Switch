namespace Excluder.Core;

public static class ExecutableSwap
{
    // Staging and backup are beside the target so File.Replace stays on the same volume.
    // The backup remains available if both startup and rollback encounter errors.
    public static async Task<string> Install(string source, string target, string oldHash, string newHash,
        string nonce, Func<Task> verifyStartup)
    {
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new InvalidDataException("Invalid update nonce.");
        Integrity.Verify(target, oldHash); Integrity.Verify(source, newHash);
        var temporary = target + "." + nonce + ".new";
        var backup = target + "." + nonce + ".previous";
        bool replaced = false;
        try
        {
            File.Copy(source, temporary, false); Integrity.Verify(temporary, newHash);
            File.Replace(temporary, target, backup); replaced = true;
            await verifyStartup();
            return backup;
        }
        catch (Exception failure)
        {
            if (replaced)
            {
                try { File.Move(backup, target, true); }
                catch (Exception rollback) { throw new AggregateException("Rollback failed. Previous executable is preserved at " + backup, failure, rollback); }
            }
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
