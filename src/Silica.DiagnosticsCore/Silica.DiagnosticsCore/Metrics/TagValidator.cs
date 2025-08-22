namespace Silica.DiagnosticsCore.Metrics
{
    public sealed class TagValidator
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _allowed;
        private readonly int _maxTagsPerMetric;
        private readonly int _maxStringLen;
        private Action<int>? _onTagRejectedCount;
        private Action<int>? _onTagTruncatedCount;
        public void SetCallbacks(Action<int>? onTagRejectedCount = null, Action<int>? onTagTruncatedCount = null)
        {
            _onTagRejectedCount = onTagRejectedCount;
            _onTagTruncatedCount = onTagTruncatedCount;
        }
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
            int rejected = 0;
            int truncated = 0;
            int accepted = 0;

            for (int i = 0; i < tags.Length; i++)
            {
                var (k, v) = tags[i];
                if (string.IsNullOrWhiteSpace(k) || !_allowed.ContainsKey(k)) { rejected++; continue; }
                if (accepted >= _maxTagsPerMetric) { rejected++; continue; }

                switch (v)
                {
                    case null:
                        rejected++; continue;
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
                    case float or double:
                        tmp.Add(new KeyValuePair<string, object>(k, v));
                        accepted++;
                        break;
                    case decimal dec:
                    {
                        var sDec = ((double)dec).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var over2 = sDec.Length > _maxStringLen;
                        tmp.Add(new KeyValuePair<string, object>(k, over2 ? sDec[.._maxStringLen] : sDec));
                        if (over2) truncated++;
                        accepted++;
                        break;
                    }
                    case Guid g:
                    {
                        var sGuid = g.ToString("D");
                        var over2 = sGuid.Length > _maxStringLen;
                        tmp.Add(new KeyValuePair<string, object>(k, over2 ? sGuid[.._maxStringLen] : sGuid));
                        if (over2) truncated++;
                        accepted++;
                        break;
                    }
                    case Enum e:
                    {
                        var sEnum = e.ToString();
                        var over2 = sEnum.Length > _maxStringLen;
                        tmp.Add(new KeyValuePair<string, object>(k, over2 ? sEnum[.._maxStringLen] : sEnum));
                        if (over2) truncated++;
                        accepted++;
                        break;
                    }
                    case DateTime dt:
                    {
                        var sDt = dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                        var over2 = sDt.Length > _maxStringLen;
                        tmp.Add(new KeyValuePair<string, object>(k, over2 ? sDt[.._maxStringLen] : sDt));
                        if (over2) truncated++;
                        accepted++;
                        break;
                    }
                    case DateTimeOffset dto:
                    {
                        var sDto = dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                        var over2 = sDto.Length > _maxStringLen;
                        tmp.Add(new KeyValuePair<string, object>(k, over2 ? sDto[.._maxStringLen] : sDto));
                        if (over2) truncated++;
                        accepted++;
                        break;
                    }
                    case TimeSpan ts:
                    {
                        var sTs = ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
                        var over2 = sTs.Length > _maxStringLen;
                        tmp.Add(new KeyValuePair<string, object>(k, over2 ? sTs[.._maxStringLen] : sTs));
                        if (over2) truncated++;
                        accepted++;
                        break;
                    }                    default:
                        rejected++; continue;
                }
            }
            // Account tags dropped due to disallowed/too many/invalid
            if (rejected > 0) _onTagRejectedCount?.Invoke(rejected);
            if (truncated > 0) _onTagTruncatedCount?.Invoke(truncated);
            return tmp.Count == 0 ? Array.Empty<KeyValuePair<string, object>>() : tmp.ToArray();
        }
    }
}