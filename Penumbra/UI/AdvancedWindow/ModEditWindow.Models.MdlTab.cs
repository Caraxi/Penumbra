using OtterGui;
using Penumbra.GameData;
using Penumbra.GameData.Files;
using Penumbra.Mods;
using Penumbra.String.Classes;

namespace Penumbra.UI.AdvancedWindow;

public partial class ModEditWindow
{
    private partial class MdlTab : IWritable
    {
        private ModEditWindow _edit;

        public readonly MdlFile Mdl;
        public readonly List<Utf8GamePath> GamePaths;
        private readonly List<string>[] _attributes;
        
        public bool PendingIo { get; private set; } = false;
        public string? IoException { get; private set; } = null;

        // TODO: this can probably be genericised across all of chara
        [GeneratedRegex(@"chara/equipment/e(?'Set'\d{4})/model/c(?'Race'\d{4})e\k'Set'_.+\.mdl", RegexOptions.Compiled)]
        private static partial Regex CharaEquipmentRegex();

        public MdlTab(ModEditWindow edit, byte[] bytes, string path, Mod? mod)
        {
            _edit       = edit;

            Mdl         = new MdlFile(bytes);
            GamePaths   = mod == null ? new() : FindGamePaths(path, mod);
            _attributes = CreateAttributes(Mdl);
        }

        /// <inheritdoc/>
        public bool Valid
            => Mdl.Valid;

        /// <inheritdoc/>
        public byte[] Write()
            => Mdl.Write();

        // TODO: this _needs_ to be done asynchronously, kart mods hang for a good second or so
        /// <summary> Find the list of game paths that may correspond to this model. </summary>
        /// <param name="path"> Resolved path to a .mdl. </param>
        /// <param name="mod"> Mod within which the .mdl is resolved. </param>
        private List<Utf8GamePath> FindGamePaths(string path, Mod mod)
        {
            // todo: might be worth ordering based on prio + selection for disambiguating between multiple matches? not sure. same for the multi group case
            return mod.AllSubMods
                .SelectMany(submod => submod.Files.Concat(submod.FileSwaps))
                // todo: using ordinal ignore case because the option group paths in mods being lowerecased somewhere, but the mod editor using fs paths, which may be uppercase. i'd say this will blow up on linux, but it's already the case so can't be too much worse than present right
                .Where(kv => kv.Value.FullName.Equals(path, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToList();
        }

        /// <summary> Export model to an interchange format. </summary>
        /// <param name="outputPath"> Disk path to save the resulting file to. </param>
        public void Export(string outputPath)
        {
            // NOTES ON EST (i don't think it's worth supporting yet...)
            // for collection wide lookup;
            // Collections.Cache.EstCache::GetEstEntry
            // Collections.Cache.MetaCache::GetEstEntry
            // Collections.ModCollection.MetaCache?
            // for default lookup, probably;
            // EstFile.GetDefault(...)
            
            // TODO: allow user to pick the gamepath in the ui
            // TODO: what if there's no gamepaths?
            var mdlPath = GamePaths.First();
            var sklbPath = GetSklbPath(mdlPath.ToString());
            var sklb = sklbPath != null ? ReadSklb(sklbPath) : null;

            PendingIo = true;
            _edit._models.ExportToGltf(Mdl, sklb, outputPath)
                .ContinueWith(task => {
                    IoException = task.Exception?.ToString();
                    PendingIo = false;
                });
        }

        /// <summary> Try to find the .sklb path for a .mdl file. </summary>
        /// <param name="mdlPath"> .mdl file to look up the skeleton for. </param>
        private string? GetSklbPath(string mdlPath)
        {
            // TODO: This needs to be drastically expanded, it's dodgy af rn

            var match = CharaEquipmentRegex().Match(mdlPath);
            if (!match.Success)
                return null;

            var race = match.Groups["Race"].Value;

            return $"chara/human/c{race}/skeleton/base/b0001/skl_c{race}b0001.sklb";
        }

        /// <summary> Read a .sklb from the active collection or game. </summary>
        /// <param name="sklbPath"> Game path to the .sklb to load. </param>
        private SklbFile ReadSklb(string sklbPath)
        {
            // TODO: if cross-collection lookups are turned off, this conversion can be skipped
            if (!Utf8GamePath.FromString(sklbPath, out var utf8SklbPath, true))
                throw new Exception("TODO: handle - should it throw, or try to fail gracefully?");
 
            var resolvedPath = _edit._activeCollections.Current.ResolvePath(utf8SklbPath);
            // TODO: is it worth trying to use streams for these instead? i'll need to do this for mtrl/tex too, so might be a good idea. that said, the mtrl reader doesn't accept streams, so...
            var bytes = resolvedPath switch
            {
                null => _edit._dalamud.GameData.GetFile(sklbPath)?.Data,
                FullPath path => File.ReadAllBytes(path.ToPath()),
            };
            if (bytes == null)
                throw new Exception("TODO: handle - this effectively means that the resolved path doesn't exist. graceful?");

            return new SklbFile(bytes);
        }

        /// <summary> Remove the material given by the index. </summary>
        /// <remarks> Meshes using the removed material are redirected to material 0, and those after the index are corrected. </remarks>
        public void RemoveMaterial(int materialIndex)
        {
            for (var meshIndex = 0; meshIndex < Mdl.Meshes.Length; meshIndex++)
            {
                var newIndex = Mdl.Meshes[meshIndex].MaterialIndex;
                if (newIndex == materialIndex)
                    newIndex = 0;
                else if (newIndex > materialIndex)
                    --newIndex;

                Mdl.Meshes[meshIndex].MaterialIndex = newIndex;
            }

            Mdl.Materials = Mdl.Materials.RemoveItems(materialIndex);
        }

        /// <summary> Create a list of attributes per sub mesh. </summary>
        private static List<string>[] CreateAttributes(MdlFile mdl)
            => mdl.SubMeshes.Select(s => Enumerable.Range(0, 32)
                .Where(idx => ((s.AttributeIndexMask >> idx) & 1) == 1)
                .Select(idx => mdl.Attributes[idx])
                .ToList()
            ).ToArray();

        /// <summary> Obtain the attributes associated with a sub mesh by its index. </summary>
        public IReadOnlyList<string> GetSubMeshAttributes(int subMeshIndex)
            => _attributes[subMeshIndex];

        /// <summary> Remove or add attributes from a sub mesh by its index. </summary>
        /// <param name="subMeshIndex"> The index of the sub mesh to update. </param>
        /// <param name="old"> If non-null, remove this attribute. </param>
        /// <param name="new"> If non-null, add this attribute. </param>
        public void UpdateSubMeshAttribute(int subMeshIndex, string? old, string? @new)
        {
            var attributes = _attributes[subMeshIndex];

            if (old != null)
                attributes.Remove(old);

            if (@new != null)
                attributes.Add(@new);

            PersistAttributes();
        }

        /// <summary> Apply changes to attributes to the file in memory. </summary>
        private void PersistAttributes()
        {
            var allAttributes = new List<string>();

            foreach (var (attributes, subMeshIndex) in _attributes.WithIndex())
            {
                var mask = 0u;

                foreach (var attribute in attributes)
                {
                    var attributeIndex = allAttributes.IndexOf(attribute);
                    if (attributeIndex == -1)
                    {
                        allAttributes.Add(attribute);
                        attributeIndex = allAttributes.Count - 1;
                    }

                    mask |= 1u << attributeIndex;
                }

                Mdl.SubMeshes[subMeshIndex].AttributeIndexMask = mask;
            }

            Mdl.Attributes = [.. allAttributes];
        }
    }
}
