namespace Silica.DiagnosticsCore.Metrics
{
    public sealed class TagValidator
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _allowed;
        private readonly int _maxTagsPerMetric;
        private readonly int _maxStringLen;
        private Action<int>? _onTagRejectedCount;
        private Action<int>? _onTagTruncatedCount;
        // Optional, per-cause callbacks for operator clarity (backward compatible)
        private Action<int>? _onRejectedInvalidKey;
        private Action<int>? _onRejectedTooMany;
        private Action<int>? _onRejectedInvalidType;
        private Action<int>? _onRejectedValueTooLong;
        public TagValidator(
            IEnumerable<string> baseAllowedKeys,
            int maxTagsPerMetric,
            int maxStringLen,
            Action<int>? onTagDropCount = null)
        {
            _allowed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            if (baseAllowedKeys is not null)
                foreach (var k in baseAllowedKeys)
                    if (!string.IsNullOrWhiteSpace(k)) _allowed.TryAdd(k, 0);
            _maxTagsPerMetric = Math.Max(0, maxTagsPerMetric);
            _maxStringLen = Math.Max(1, maxStringLen);
            _onTagRejectedCount = onTagDropCount;
        }

        // Backward-compatible callback wiring with optional per-cause detail
        public void SetCallbacks(
            Action<int>? onTagRejectedCount = null,
            Action<int>? onTagTruncatedCount = null,
            Action<int>? onRejectedInvalidKey = null,
            Action<int>? onRejectedTooMany = null,
            Action<int>? onRejectedInvalidType = null,
            Action<int>? onRejectedValueTooLong = null)
        {
            if (onTagRejectedCount is not null) _onTagRejectedCount = onTagRejectedCount;
            if (onTagTruncatedCount is not null) _onTagTruncatedCount = onTagTruncatedCount;

            _onRejectedInvalidKey = onRejectedInvalidKey;
            _onRejectedTooMany = onRejectedTooMany;
            _onRejectedInvalidType = onRejectedInvalidType;
            _onRejectedValueTooLong = onRejectedValueTooLong;
        }

        public void AddAllowedKeys(IEnumerable<string> keys)
        {
            if (keys is null) return;
            foreach (var k in keys)
                if (!string.IsNullOrWhiteSpace(k))
                    _allowed.TryAdd(k, 0);
        }

        public KeyValuePair<string, object>[] Validate(KeyValuePair<string, object>[]? tags)
        {
            if (tags is null || tags.Length == 0)
                return Array.Empty<KeyValuePair<string, object>>();

            var tmp = new List<KeyValuePair<string, object>>(_maxTagsPerMetric);
            int rejectedTotal = 0;
            int rejectedInvalidKey = 0;
            int rejectedTooMany = 0;
            int rejectedInvalidType = 0;
            int truncated = 0;
            int accepted = 0;
            int rejectedValueTooLong = 0;

            for (int i = 0; i < tags.Length; i++)
            {
                var (k, v) = tags[i];
                if (string.IsNullOrWhiteSpace(k) || !_allowed.TryGetValue(k, out _))
                { rejectedTotal++; rejectedInvalidKey++; continue; }
                if (accepted >= _maxTagsPerMetric)
                { rejectedTotal++; rejectedTooMany++; continue; }

                switch (v)
                {
                    case null:
                        rejectedTotal++; rejectedInvalidType++; continue;
                    case string s:
                        var over = s.Length > _maxStringLen;
                        var value = over ? s[.._maxStringLen] : s;
                        tmp.Add(new KeyValuePair<string, object>(k, value));
                        if (over) truncated++;
                        accepted++;
                        break;
                    case sbyte or byte or short or ushort or int or uint or long or ulong or bool:
                        tmp.Add(new KeyValuePair<string, object>(k, v));
                        accepted++;
                        break;
                    case decimal dec:
                    {
                        var sDec = dec.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
                        if (sDec.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sDec));
                        accepted++;
                        break;
                    }
                    case float f:
                    {
                        var sF = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        if (sF.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sF));
                        accepted++;
                        break;
                    }
                    case double d:
                    {
                        var sD = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        if (sD.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sD));
                        accepted++;
                        break;
                    }
                    case Guid g:
                    {
                        var sGuid = g.ToString("D");
                        if (sGuid.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sGuid));
                        accepted++;
                        break;
                    }
                    case Enum e:
                    {
                        var sEnum = e.ToString();
                        if (sEnum.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sEnum));
                        accepted++;
                        break;
                    }
                    case DateTime dt:
                    {
                        var sDt = dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                        if (sDt.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sDt));
                        accepted++;
                        break;
                    }
                    case DateTimeOffset dto:
                    {
                        var sDto = dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                        if (sDto.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sDto));
                        accepted++;
                        break;
                    }
                    case TimeSpan ts:
                    {
                        var sTs = ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
                        if (sTs.Length > _maxStringLen) { rejectedTotal++; rejectedValueTooLong++; continue; }
                        tmp.Add(new KeyValuePair<string, object>(k, sTs));
                        accepted++;
                        break;
                    }
                    default:
                        rejectedTotal++; rejectedInvalidType++; continue;
                }
            }
            // Account tags dropped due to disallowed/too many/invalid
            if (rejectedTotal > 0) _onTagRejectedCount?.Invoke(rejectedTotal);
            if (rejectedInvalidKey > 0) _onRejectedInvalidKey?.Invoke(rejectedInvalidKey);
            if (rejectedTooMany > 0) _onRejectedTooMany?.Invoke(rejectedTooMany);
            if (rejectedValueTooLong > 0) _onRejectedValueTooLong?.Invoke(rejectedValueTooLong);
            // Account tags truncated due to length policy
            if (truncated > 0) _onTagTruncatedCount?.Invoke(truncated);
            return tmp.Count == 0 ? Array.Empty<KeyValuePair<string, object>>() : tmp.ToArray();
        }
    }
}