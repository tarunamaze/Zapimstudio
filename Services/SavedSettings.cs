using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Zapim.Marketing;

// Settings an administrator saves from the Settings page: one JSON file in the storage directory, with secrets encrypted
// like social tokens (so they need the same key ring). When nothing is saved, subclasses fall back to server configuration.
public abstract class SavedSettings<T>(Storage storage, IDataProtectionProvider protection, string fileName, string purpose) where T : class
{
    private readonly Lock gate = new();
    private T? saved;
    private bool loaded;
    private string FilePath => Path.Combine(storage.Root, fileName);
    protected IDataProtector Protector => protection.CreateProtector(purpose);

    protected T? Load()
    {
        lock (gate)
        {
            if (!loaded) { saved = File.Exists(FilePath) ? JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath)) : null; loaded = true; }
            return saved;
        }
    }
    protected void Write(T value)
    {
        lock (gate) { File.WriteAllText(FilePath, JsonSerializer.Serialize(value)); saved = value; loaded = true; }
    }
    public void Clear() { lock (gate) { File.Delete(FilePath); saved = null; loaded = true; } }
}
