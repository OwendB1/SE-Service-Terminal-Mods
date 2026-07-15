using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using VRage.Utils;
using VRageMath;
using VRageMath.PackedVector;
using VRageRender.Import;

namespace ServiceTerminalModelPatcher
{
    internal static class Program
    {
        private const float DetectorGapFraction = 0.04f;
        private const float ScreenGapFraction = 0.04f;
        private const string ServicesDummy = "detector_services_01";
        private const string InsuranceDummy = "detector_shipinsurance_01";
        private const string ServicesHighlight = "Screen_section_services";
        private const string InsuranceHighlight = "Screen_section_insurance";
        private const string ServicesScreenMaterial = "CockpitScreen_01";
        private const string InsuranceScreenMaterial = "CockpitScreen_02";
        private const string HighlightMaterial = "CockpitFighter_Interior";
        private const string GeometryAsset =
            @"Models\Cubes\Large\ServicesTerminalInsurance_LOD0";

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine(
                    "Usage: ServiceTerminalModelPatcher <input.mwm> <output.mwm>");
                return 2;
            }

            string input = Path.GetFullPath(args[0]);
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            MyModelImporter importer = new MyModelImporter();
            importer.ImportData(input);
            Dictionary<string, object> tags = importer.GetTagData();
            Dictionary<string, MyModelDummy> dummies =
                (Dictionary<string, MyModelDummy>)tags["Dummies"];
            SplitDetectors(dummies);

            string sourceGeometry = ResolveGeometry(input, (string)tags["GeometryDataAsset"]);
            string outputDirectory = Path.GetDirectoryName(output);
            string outputGeometry = null;
            for (int lod = 0; lod <= 3; lod++)
            {
                string sourceLod = lod == 0 ? sourceGeometry : Path.Combine(
                    Path.GetDirectoryName(sourceGeometry), "ServicesTerminal_LOD" + lod + ".mwm");
                string outputLod = Path.Combine(outputDirectory,
                    "ServicesTerminalInsurance_LOD" + lod + ".mwm");
                PatchGeometry(sourceLod, outputLod);
                if (lod == 0) outputGeometry = outputLod;
            }

            tags["GeometryDataAsset"] = GeometryAsset;
            RedirectLods(tags);
            tags["Sections"] = CreateHighlightSections();
            MyModelExporter.ExportModelData(output, tags, false);
            Validate(output, outputGeometry);

            Console.WriteLine("Wrote split terminal model: " + output);
            Console.WriteLine("Wrote split screen geometry: " + outputGeometry);
            return 0;
        }

        private static void PatchGeometry(string source, string output)
        {
            MyModelImporter importer = new MyModelImporter();
            importer.ImportData(source);
            Dictionary<string, object> tags = importer.GetTagData();
            SplitScreenGeometry(tags);
            RedirectLods(tags);
            MyModelExporter.ExportModelData(output, tags, true);
            ValidateGeometry(output);
        }

        private static void RedirectLods(Dictionary<string, object> tags)
        {
            MyLODDescriptor[] lods = (MyLODDescriptor[])tags["LODs"];
            for (int i = 0; i < lods.Length; i++)
            {
                string file = Path.GetFileName(lods[i].Model.Replace('\\', '/'));
                int suffix = file.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
                if (suffix < 0) continue;
                lods[i].Model = @"Models\Cubes\Large\ServicesTerminalInsurance" +
                                file.Substring(suffix);
            }
        }

        private static void SplitDetectors(Dictionary<string, MyModelDummy> dummies)
        {
            MyModelDummy original;
            if (!dummies.TryGetValue(ServicesDummy, out original))
                throw new InvalidDataException("Missing " + ServicesDummy);

            Matrix services = original.Matrix;
            Matrix insurance = original.Matrix;
            Vector3 originalUp = original.Matrix.Up;
            float halfScale = (1f - DetectorGapFraction) * 0.5f;
            // Dummy matrix axes describe the full detector box size, so each
            // half moves by one quarter of the original height.
            float centerScale = (1f + DetectorGapFraction) * 0.25f;

            services.Up *= halfScale;
            services.Translation += originalUp * centerScale;
            original.Matrix = services;
            original.CustomData = WithHighlight(original.CustomData, ServicesHighlight);

            insurance.Up *= halfScale;
            insurance.Translation -= originalUp * centerScale;
            dummies[InsuranceDummy] = new MyModelDummy
            {
                Name = InsuranceDummy,
                CustomData = WithHighlight(original.CustomData, InsuranceHighlight),
                Matrix = insurance
            };

            float gap = Vector3.Distance(services.Translation, insurance.Translation) -
                        services.Up.Length() * 0.5f - insurance.Up.Length() * 0.5f;
            if (gap <= 0f)
                throw new InvalidDataException("Detector halves overlap.");

            Console.WriteLine("Services detector: " + Describe(services));
            Console.WriteLine("Insurance detector: " + Describe(insurance));
            Console.WriteLine("Detector gap: " + gap.ToString("0.0000") + " m");
        }

        private static IReadOnlyDictionary<string, object> WithHighlight(
            IReadOnlyDictionary<string, object> source, string highlight)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in source) data[pair.Key] = pair.Value;
            data["highlight"] = highlight;
            return new ReadOnlyDictionary<string, object>(data);
        }

        private static void SplitScreenGeometry(Dictionary<string, object> tags)
        {
            List<MyMeshPartInfo> parts = (List<MyMeshPartInfo>)tags["MeshParts"];
            MyMeshPartInfo screen = FindPart(parts, ServicesScreenMaterial);
            MyMeshPartInfo highlight = FindPart(parts, HighlightMaterial);

            QuadSplit screenSplit = SplitQuad(tags, screen);
            screen.m_indices.Clear();
            screen.m_indices.AddRange(screenSplit.Upper);
            MyMeshPartInfo insuranceScreen = ClonePart(screen, InsuranceScreenMaterial);
            insuranceScreen.m_indices.Clear();
            insuranceScreen.m_indices.AddRange(screenSplit.Lower);
            parts.Add(insuranceScreen);

            QuadSplit highlightSplit = SplitQuad(tags, highlight);
            highlight.m_indices.Clear();
            highlight.m_indices.AddRange(highlightSplit.Upper);
            highlight.m_indices.AddRange(highlightSplit.Lower);
            tags["Sections"] = CreateHighlightSections();

            MyModelInfo info = (MyModelInfo)tags["ModelInfo"];
            info.VerticesCount = ((HalfVector4[])tags["Vertices"]).Length;
            int indices = 0;
            for (int i = 0; i < parts.Count; i++) indices += parts[i].m_indices.Count;
            info.TrianglesCount = indices / 3;
        }

        private static QuadSplit SplitQuad(Dictionary<string, object> tags, MyMeshPartInfo part)
        {
            if (part.m_indices.Count != 6)
                throw new InvalidDataException(part.GetMaterialName() +
                    " is no longer a six-index screen quad.");

            HalfVector4[] vertices = (HalfVector4[])tags["Vertices"];
            List<int> unique = new List<int>();
            for (int i = 0; i < part.m_indices.Count; i++)
                if (!unique.Contains(part.m_indices[i])) unique.Add(part.m_indices[i]);
            if (unique.Count != 4)
                throw new InvalidDataException(part.GetMaterialName() +
                    " does not have four unique screen vertices.");

            int topLeft = FindCorner(vertices, unique, true, false);
            int topRight = FindCorner(vertices, unique, true, true);
            int bottomLeft = FindCorner(vertices, unique, false, false);
            int bottomRight = FindCorner(vertices, unique, false, true);
            Vector4 topLeftPosition = vertices[topLeft].ToVector4();
            Vector4 bottomLeftPosition = vertices[bottomLeft].ToVector4();
            float minY = bottomLeftPosition.Y;
            float maxY = topLeftPosition.Y;
            float middle = (minY + maxY) * 0.5f;
            float halfGap = (maxY - minY) * ScreenGapFraction * 0.5f;

            int upperLeft = AddSplitVertex(tags, bottomLeft, topLeft,
                middle + halfGap, false);
            int upperRight = AddSplitVertex(tags, bottomRight, topRight,
                middle + halfGap, false);
            int lowerLeft = AddSplitVertex(tags, bottomLeft, topLeft,
                middle - halfGap, true);
            int lowerRight = AddSplitVertex(tags, bottomRight, topRight,
                middle - halfGap, true);

            return new QuadSplit
            {
                Upper = new List<int>
                {
                    topRight, upperRight, upperLeft,
                    topRight, upperLeft, topLeft
                },
                Lower = new List<int>
                {
                    lowerRight, bottomRight, bottomLeft,
                    lowerRight, bottomLeft, lowerLeft
                }
            };
        }

        private static int AddSplitVertex(Dictionary<string, object> tags, int bottom,
            int top, float y, bool topUv)
        {
            HalfVector4[] vertices = (HalfVector4[])tags["Vertices"];
            Vector4 bottomPosition = vertices[bottom].ToVector4();
            Vector4 topPosition = vertices[top].ToVector4();
            float amount = (y - bottomPosition.Y) /
                           (topPosition.Y - bottomPosition.Y);
            Vector4 position = Vector4.Lerp(bottomPosition, topPosition, amount);
            HalfVector2[] texCoords = (HalfVector2[])tags["TexCoords0"];
            Vector2 uv = texCoords[topUv ? top : bottom].ToVector2();
            int index = vertices.Length;

            Append(tags, "Vertices", new HalfVector4(position));
            AppendCopy<Byte4>(tags, "Normals", bottom);
            Append(tags, "TexCoords0", new HalfVector2(uv));
            AppendCopy<Byte4>(tags, "Binormals", bottom);
            AppendCopy<Byte4>(tags, "Tangents", bottom);
            AppendCopy<HalfVector2>(tags, "TexCoords1", bottom);
            AppendCopy<Vector4I>(tags, "BlendIndices", bottom);
            AppendCopy<Vector4>(tags, "BlendWeights", bottom);
            return index;
        }

        private static int FindCorner(HalfVector4[] vertices, List<int> candidates,
            bool top, bool right)
        {
            int best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                Vector4 value = vertices[candidates[i]].ToVector4();
                Vector4 current = vertices[best].ToVector4();
                bool betterY = top ? value.Y > current.Y : value.Y < current.Y;
                if (betterY || Math.Abs(value.Y - current.Y) < 0.0001f &&
                    (right ? value.X > current.X : value.X < current.X))
                    best = candidates[i];
            }
            return best;
        }

        private static MyMeshPartInfo ClonePart(MyMeshPartInfo source, string materialName)
        {
            MyMaterialDescriptor descriptor = new MyMaterialDescriptor(materialName)
            {
                Technique = source.m_MaterialDesc.Technique,
                GlassCW = source.m_MaterialDesc.GlassCW,
                GlassCCW = source.m_MaterialDesc.GlassCCW,
                GlassSmoothNormals = source.m_MaterialDesc.GlassSmoothNormals
            };
            foreach (KeyValuePair<string, string> texture in source.m_MaterialDesc.Textures)
                descriptor.Textures[texture.Key] = texture.Value;
            foreach (KeyValuePair<string, string> data in source.m_MaterialDesc.UserData)
                descriptor.UserData[data.Key] = data.Value;

            return new MyMeshPartInfo
            {
                m_MaterialHash = MyStringHash.GetOrCompute(materialName).GetHashCode(),
                m_MaterialDesc = descriptor,
                Technique = source.Technique
            };
        }

        private static List<MyMeshSectionInfo> CreateHighlightSections()
        {
            return new List<MyMeshSectionInfo>
            {
                CreateHighlightSection(ServicesHighlight, 0),
                CreateHighlightSection(InsuranceHighlight, 6)
            };
        }

        private static MyMeshSectionInfo CreateHighlightSection(string name, int start)
        {
            MyMeshSectionInfo section = new MyMeshSectionInfo { Name = name };
            section.Meshes.Add(new MyMeshSectionMeshInfo
            {
                MaterialName = HighlightMaterial,
                StartIndex = start,
                IndexCount = 6
            });
            return section;
        }

        private static MyMeshPartInfo FindPart(List<MyMeshPartInfo> parts, string name)
        {
            for (int i = 0; i < parts.Count; i++)
                if (string.Equals(parts[i].GetMaterialName(), name,
                    StringComparison.Ordinal)) return parts[i];
            throw new InvalidDataException("Missing model material " + name);
        }

        private static string ResolveGeometry(string parent, string asset)
        {
            string fileName = Path.GetFileName(asset.Replace('\\', '/'));
            if (!fileName.EndsWith(".mwm", StringComparison.OrdinalIgnoreCase))
                fileName += ".mwm";
            string path = Path.Combine(Path.GetDirectoryName(parent), fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException("Missing source geometry", path);
            return path;
        }

        private static void Append<T>(Dictionary<string, object> tags, string key, T value)
        {
            T[] values = (T[])tags[key];
            Array.Resize(ref values, values.Length + 1);
            values[values.Length - 1] = value;
            tags[key] = values;
        }

        private static void AppendCopy<T>(Dictionary<string, object> tags, string key,
            int sourceIndex)
        {
            T[] values = (T[])tags[key];
            if (values.Length == 0) return;
            Append(tags, key, values[sourceIndex]);
        }

        private static void Validate(string modelPath, string geometryPath)
        {
            MyModelImporter modelImporter = new MyModelImporter();
            modelImporter.ImportData(modelPath);
            Dictionary<string, object> modelTags = modelImporter.GetTagData();
            Dictionary<string, MyModelDummy> dummies =
                (Dictionary<string, MyModelDummy>)modelTags["Dummies"];
            if (!Equals(dummies[ServicesDummy].CustomData["highlight"], ServicesHighlight) ||
                !Equals(dummies[InsuranceDummy].CustomData["highlight"], InsuranceHighlight))
                throw new InvalidDataException("Detector highlight sections were not exported.");

            ValidateGeometry(geometryPath);
        }

        private static void ValidateGeometry(string geometryPath)
        {
            MyModelImporter geometryImporter = new MyModelImporter();
            geometryImporter.ImportData(geometryPath);
            Dictionary<string, object> geometryTags = geometryImporter.GetTagData();
            List<MyMeshPartInfo> parts = (List<MyMeshPartInfo>)geometryTags["MeshParts"];
            if (FindPart(parts, ServicesScreenMaterial).m_indices.Count != 6 ||
                FindPart(parts, InsuranceScreenMaterial).m_indices.Count != 6)
                throw new InvalidDataException("Split screen materials were not exported.");
            List<MyMeshSectionInfo> sections =
                (List<MyMeshSectionInfo>)geometryTags["Sections"];
            if (sections.Count != 2 || sections[0].Meshes[0].IndexCount != 6 ||
                sections[1].Meshes[0].IndexCount != 6)
                throw new InvalidDataException("Split highlight sections were not exported.");

            int vertexCount = ((HalfVector4[])geometryTags["Vertices"]).Length;
            string[] streams = { "Normals", "TexCoords0", "Binormals", "Tangents" };
            for (int i = 0; i < streams.Length; i++)
            {
                Array stream = (Array)geometryTags[streams[i]];
                if (stream.Length != vertexCount)
                    throw new InvalidDataException(streams[i] + " vertex stream is inconsistent.");
            }
        }

        private static string Describe(Matrix matrix)
        {
            return "position=" + matrix.Translation +
                   ", size=" + new Vector3(matrix.Right.Length(), matrix.Up.Length(),
                       matrix.Forward.Length());
        }

        private sealed class QuadSplit
        {
            internal List<int> Upper;
            internal List<int> Lower;
        }
    }
}
