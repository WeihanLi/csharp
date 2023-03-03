using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using NSwag;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LibKubernetesGenerator
{
    internal class VersionConverterStubGenerator
    {
        private readonly ClassNameHelper classNameHelper;

        public VersionConverterStubGenerator(ClassNameHelper classNameHelper)
        {
            this.classNameHelper = classNameHelper;
        }

        public void Generate(OpenApiDocument swagger, GeneratorExecutionContext context)
        {
            var allGeneratedModelClassNames = new List<string>();

            foreach (var kv in swagger.Definitions)
            {
                var def = kv.Value;
                var clz = classNameHelper.GetClassNameForSchemaDefinition(def);
                allGeneratedModelClassNames.Add(clz);
            }

            var versionRegex = @"(^V|v)[0-9]+((alpha|beta)[0-9]+)?";
            var typePairs = allGeneratedModelClassNames
                .OrderBy(x => x)
                .Select(x => new
                {
                    Version = Regex.Match(x, versionRegex).Value?.ToLower(),
                    Kinda = Regex.Replace(x, versionRegex, string.Empty),
                    Type = x,
                })
                .Where(x => !string.IsNullOrEmpty(x.Version))
                .GroupBy(x => x.Kinda)
                .Where(x => x.Count() > 1)
                .SelectMany(x =>
                    x.SelectMany((value, index) => x.Skip(index + 1), (first, second) => new { first, second }))
                .OrderBy(x => x.first.Kinda)
                .ThenBy(x => x.first.Version)
                .Select(x => (x.first.Type, x.second.Type))
                .ToList();

            var sbmodel = new StringBuilder(@"// <auto-generated />
namespace k8s.Models;
");

            foreach (var (t0, t1) in typePairs)
            {
                sbmodel.AppendLine($@"
    public partial class {t0}
    {{
        public static explicit operator {t0}({t1} s) => ModelVersionConverter.Convert<{t1}, {t0}>(s);
    }}
    public partial class {t1}
    {{
        public static explicit operator {t1}({t0} s) => ModelVersionConverter.Convert<{t0}, {t1}>(s);
    }}");
            }

            context.AddSource($"ModelOperators.g.cs", SourceText.From(sbmodel.ToString(), Encoding.UTF8));
        }
    }
}
