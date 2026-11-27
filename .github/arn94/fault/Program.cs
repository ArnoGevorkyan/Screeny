using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2) throw new ArgumentException("Expected a test dependency and synthetic marker.");
string original = Path.GetFullPath(args[0]);
string replacement = original + ".arn94";
using (var module = ModuleDefinition.ReadModule(original, new ReaderParameters { InMemory = true }))
{
    var target = module.Types.Single(t => t.Name == "LiveCharts");
    var methods = target.Methods.Where(m => m.Name == "Configure" && m.HasBody && m.IsStatic).ToArray();
    if (methods.Length == 0) throw new InvalidOperationException("Missing LiveCharts.Configure body.");
    var ctor = module.ImportReference(typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
    foreach (var method in methods)
    {
        var il = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, args[1]));
        il.InsertBefore(first, il.Create(OpCodes.Newobj, ctor));
        il.InsertBefore(first, il.Create(OpCodes.Throw));
    }
    module.Write(replacement);
}
File.Move(replacement, original, true);
Console.WriteLine("Injected a managed failure into the disposable chart dependency only.");
