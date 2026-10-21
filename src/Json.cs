using Newtonsoft.Json;
using UnityEngine;

namespace AutoHeim
{
    internal static class Json
    {
        internal static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            FloatFormatHandling = FloatFormatHandling.DefaultValue,
        });

        internal static object Vec(Vector3 v) => new { x = R(v.x), y = R(v.y), z = R(v.z) };

        internal static float R(float f) => Mathf.Round(f * 100f) / 100f;

        /// Rounded value, or null when absent. Avoids `cond ? float : (float?)null`
        /// inside iterators, which trips a Mono JIT assertion (fconv_to_r8_x) in this game build.
        internal static object ROrNull(bool present, float f) => present ? (object)R(f) : null;
    }
}
