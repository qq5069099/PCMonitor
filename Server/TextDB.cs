using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// 基于文本文件的轻量级键值数据库。
/// 内存中维护一个 JsonObject，可选 AES 加密持久化到 .txt 文件。
/// </summary>
public sealed class TextDB
{
    private readonly string _filePath;
    private readonly string _aesKey;   // 32 chars
    private readonly string _aesIV;    // 16 chars
    private readonly bool _encrypted;

    private JsonObject _data = new();

    // ------------------------------------------------------------------ 构造

    /// <param name="filePath">存储文件路径（建议 .txt 后缀）</param>
    /// <param name="password">加密密码，空字符串表示明文存储</param>
    public TextDB(string filePath, string password = "")
    {
        _filePath = filePath;
        _encrypted = !string.IsNullOrEmpty(password);

        if (_encrypted)
        {
            // 由密码派生固定长度的 AES Key(32) 和 IV(16)
            string hash = Utility.MD5(password);                    // 32 hex chars
            string hashIV = Utility.MD5(password + "_textdb_iv");   // 32 hex chars
            _aesKey = hash;               // AES-256 key
            _aesIV = hashIV[..16];        // AES IV
        }
        else
        {
            _aesKey = string.Empty;
            _aesIV = string.Empty;
        }

        Load();
    }

    // ------------------------------------------------------------------ KV 接口

    /// <summary>索引器：读写任意 JsonNode 值</summary>
    public JsonNode? this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    /// <summary>设置字符串值</summary>
    public void Set(string key, string value) => SetNode(key, JsonValue.Create(value));

    /// <summary>设置整数值</summary>
    public void Set(string key, int value) => SetNode(key, JsonValue.Create(value));

    /// <summary>设置长整数值</summary>
    public void Set(string key, long value) => SetNode(key, JsonValue.Create(value));

    /// <summary>设置浮点值</summary>
    public void Set(string key, double value) => SetNode(key, JsonValue.Create(value));

    /// <summary>设置布尔值</summary>
    public void Set(string key, bool value) => SetNode(key, JsonValue.Create(value));

    /// <summary>设置任意 JsonNode（JsonObject / JsonArray / JsonValue）</summary>
    public void Set(string key, JsonNode? value) => SetNode(key, value?.DeepClone());

    private void SetNode(string key, JsonNode? node)
    {
        _data[key] = node;
        Save();
    }

    /// <summary>获取原始 JsonNode，key 不存在时返回 null</summary>
    public JsonNode? Get(string key)
        => _data.TryGetPropertyValue(key, out var node) ? node : null;

    /// <summary>获取字符串，不存在时返回 defaultValue</summary>
    public string? GetString(string key, string? defaultValue = null)
        => _data.TryGetPropertyValue(key, out var node) ? node?.GetValue<string>() : defaultValue;

    /// <summary>获取整数，不存在时返回 defaultValue</summary>
    public int GetInt(string key, int defaultValue = 0)
        => _data.TryGetPropertyValue(key, out var node) && node != null
            ? node.GetValue<int>() : defaultValue;

    /// <summary>获取长整数，不存在时返回 defaultValue</summary>
    public long GetLong(string key, long defaultValue = 0)
        => _data.TryGetPropertyValue(key, out var node) && node != null
            ? node.GetValue<long>() : defaultValue;

    /// <summary>获取浮点数，不存在时返回 defaultValue</summary>
    public double GetDouble(string key, double defaultValue = 0.0)
        => _data.TryGetPropertyValue(key, out var node) && node != null
            ? node.GetValue<double>() : defaultValue;

    /// <summary>获取布尔值，不存在时返回 defaultValue</summary>
    public bool GetBool(string key, bool defaultValue = false)
        => _data.TryGetPropertyValue(key, out var node) && node != null
            ? node.GetValue<bool>() : defaultValue;

    /// <summary>获取 JsonObject，不存在时返回 null</summary>
    public JsonObject? GetObject(string key)
        => _data.TryGetPropertyValue(key, out var node) ? node as JsonObject : null;

    /// <summary>获取 JsonArray，不存在时返回 null</summary>
    public JsonArray? GetArray(string key)
        => _data.TryGetPropertyValue(key, out var node) ? node as JsonArray : null;

    /// <summary>是否包含指定键</summary>
    public bool ContainsKey(string key) => _data.ContainsKey(key);

    /// <summary>删除指定键，返回是否成功</summary>
    public bool Remove(string key) => _data.Remove(key);

    /// <summary>清空所有数据（不自动保存）</summary>
    public void Clear() => _data = new JsonObject();

    /// <summary>当前键数量</summary>
    public int Count => _data.Count;

    /// <summary>所有键名</summary>
    public IEnumerable<string> Keys => _data.Select(kv => kv.Key);

    // ------------------------------------------------------------------ 持久化

    /// <summary>从文件加载数据到内存（覆盖当前内存数据）</summary>
    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _data = new JsonObject();
            return;
        }

        try
        {
            string content = File.ReadAllText(_filePath, Encoding.UTF8).Trim();
            if (string.IsNullOrEmpty(content))
            {
                _data = new JsonObject();
                return;
            }

            string json;
            if (_encrypted)
            {
                byte[] cipher = Convert.FromBase64String(content);
                byte[]? plain = Utility.AESDecrypt(cipher, _aesKey, _aesIV);
                if (plain == null) throw new InvalidOperationException("AES 解密失败");
                json = Encoding.UTF8.GetString(plain);
            }
            else
            {
                json = content;
            }

            var node = JsonNode.Parse(json);
            _data = node as JsonObject ?? new JsonObject();
        }
        catch
        {
            _data = new JsonObject();
        }
    }

    /// <summary>将内存数据保存到文件</summary>
    public void Save()
    {

        if (_encrypted)
        {
            string json = _data.ToJsonStringUnsafe();
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[]? cipher = Utility.AESEncrypt(plain, _aesKey, _aesIV);
            if (cipher == null) throw new InvalidOperationException("AES 加密失败");
            string content = Convert.ToBase64String(cipher);
            WriteFile(content);
        }
        else
        {
            string json = _data.ToJsonStringFormat();
            WriteFile(json);
        }
    }

    // ------------------------------------------------------------------ 私有辅助

    private void WriteFile(string content)
    {
        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        try
        {
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 可根据需要选择抛出或记录日志
            Console.WriteLine($"[TextDB] 文件写入失败: {ex.Message}\r\n{ex.StackTrace}");
        }
    }

}

public static class JsonExtensions
{
    private static readonly JsonSerializerOptions _unsafeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions _formatOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJsonStringUnsafe<T>(this T obj)
    {
        if (obj is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.ToString();
        var result = JsonSerializer.Serialize(obj, _unsafeOptions);
        return result;
    }
    public static string ToJsonStringFormat<T>(this T obj)
    {
        if (obj is JsonValue v && v.GetValueKind() == JsonValueKind.String) return v.ToString();
        return JsonSerializer.Serialize(obj, _formatOptions);
    }

}

