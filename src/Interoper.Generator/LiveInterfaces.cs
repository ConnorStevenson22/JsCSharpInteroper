using Interoper.Generator.Ir;

namespace Interoper.Generator;

/// <summary>
/// R15: an interface with a method, its own or an interface base's, is a live type like a class, since its objects
/// can't be copied: a factory's plain object, <c>{ dispose() {…} }</c>. It becomes a class without constructors,
/// marked <see cref="ClassDeclaration.Interface"/>. Its first such base is its base class, and the members of its other
/// bases are its own. Not an event payload or a payload's base (R24), which stay data records without their methods, nor
/// one whose methods are all iterators, a callable one, or one that extends a class.
/// </summary>
internal static class LiveInterfaces
{
    public static IrDocument Convert(IrDocument ir)
    {
        var all = new Dictionary<(string Module, string Name), Declaration>();
        foreach (var m in ir.Modules)
        {
            foreach (var d in m.Declarations)
            {
                all[(m.Specifier, d.Name)] = d;
            }
        }
        Declaration? Find(IrType type) => type is RefType { Module: { } module } r ? all.GetValueOrDefault((module, r.Name)) : null;

        var payloads = new HashSet<InterfaceDeclaration>(ReferenceEqualityComparer.Instance);
        var events = new Stack<IrType>(all.Values.OfType<ClassDeclaration>().SelectMany(c => c.Events).Select(e => e.Type));
        while (events.TryPop(out var type))
        {
            if (Find(type) is InterfaceDeclaration i && payloads.Add(i))
            {
                i.Extends.ToList().ForEach(events.Push);
            }
        }

        var live = new Dictionary<InterfaceDeclaration, bool>(ReferenceEqualityComparer.Instance);
        bool IsLive(InterfaceDeclaration i)
        {
            if (live.TryGetValue(i, out var known))
            {
                return known;
            }
            live[i] = false; // an interface that extends itself isn't live through itself
            var bases = i.Extends.Select(Find).ToList();
            return live[i] = !payloads.Contains(i) && i.CallSignatures.Count + i.ConstructSignatures.Count == 0 && !bases.Any(b => b is ClassDeclaration) &&
                (i.Members.Any(m => m is MethodMember && !Selection.IsIterator(m)) || bases.OfType<InterfaceDeclaration>().Any(IsLive));
        }

        // An interface's members, then its interface bases', each name once.
        IEnumerable<Member> Members(IrType type, HashSet<Declaration> seen) =>
            Find(type) is InterfaceDeclaration i && seen.Add(i) ? i.Members.Concat(i.Extends.SelectMany(b => Members(b, seen))) : [];

        Declaration Convert(Declaration d)
        {
            if (d is not InterfaceDeclaration i || !IsLive(i))
            {
                return d;
            }
            var primary = i.Extends.FirstOrDefault(b => Find(b) is InterfaceDeclaration based && IsLive(based));
            var seen = new HashSet<Declaration>(ReferenceEqualityComparer.Instance);
            var names = i.Members.Concat(primary is null ? [] : Members(primary, seen)).Select(m => m.Name).ToHashSet();
            var inherited = i.Extends.Where(b => !ReferenceEquals(b, primary)).SelectMany(b => Members(b, seen))
                .Where(m => names.Add(m.Name)).Select(m => m with { Inherited = true });
            return new ClassDeclaration
            {
                Name = i.Name,
                Export = i.Export,
                ImportFrom = i.ImportFrom,
                Doc = i.Doc,
                TypeParameters = i.TypeParameters,
                Extends = primary,
                Members = [.. i.Members, .. inherited],
                Interface = true,
            };
        }

        return ir with { Modules = [.. ir.Modules.Select(m => m with { Declarations = [.. m.Declarations.Select(Convert)] })] };
    }
}
