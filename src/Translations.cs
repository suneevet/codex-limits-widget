using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace CodexUsageTaskbar
{
    public static partial class Ui
    {
        public static bool English
        {
            get {
#if ENGLISH
                return true;
#else
                return false;
#endif
            }
        }
        static readonly Dictionary<string, string> translations = Load();
        static Dictionary<string, string> Load()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("translations.en.json"))
            using (var reader = new StreamReader(stream))
                return new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        }
        public static string Text(string korean)
        {
            if (!English) return korean;
            string english;
            if (!translations.TryGetValue(korean, out english)) throw new InvalidOperationException("Missing English translation: " + korean);
            return english;
        }
    }
}
