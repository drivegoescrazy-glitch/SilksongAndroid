using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModWeaver;

/// <summary>
/// The port's own weaves: patches that are part of the build rather than part
/// of somebody's mod folder.
///
/// ── why this exists at all ─────────────────────────────────────────────────
///
/// Silksong clamps the shape it will render into. ForceCameraAspect:
///
///     AutoScaleViewportShared:
///         MinMaxFloat(1.6f, 2.3916667f).GetClampedBetween(w / (float)h)
///
/// Anything narrower than 1.6 : 1 is letterboxed by the game itself, on every
/// platform. That is Team Cherry's framing decision and on a 16:9 monitor it
/// never comes up -- but Android is not 16:9. A Galaxy Z Fold's inner screen is
/// 2160x1856, which is 1.164 : 1, and a 4:3 handheld is 1.333 : 1. Both sit
/// below the floor, so both keep black bars no matter how perfectly the window
/// and the render target are matched. Measured on a Fold 6: 253 px top and
/// bottom, 13.6%, exactly (1 - 1.164/1.6) / 2.
///
/// ── why it is woven rather than patched at runtime ─────────────────────────
///
/// The constant is baked into IL, and by the time the game runs there is no IL
/// left -- il2cpp has turned it into C++ and then into a .so. The only moment
/// it can be changed is the one this tool already owns: after the depot's
/// assemblies are staged and before il2cpp is handed them.
///
/// ── why it is still a runtime toggle ───────────────────────────────────────
///
/// Because a constant is not the only thing IL can hold. Rather than writing a
/// different number in, the load is replaced with a CALL to a gate injected
/// beside it, and the gate answers from a field the game's own C# can set at
/// startup. So the weave is unconditional and free, and turning the feature on
/// and off costs a relaunch rather than the twenty minutes an il2cpp
/// conversion costs. That is the same bargain Mods.gates strikes for plugins,
/// and for the same reason.
///
/// ── how it fails ───────────────────────────────────────────────────────────
///
/// Safely, in both directions. An unset gate reads zero, and the gate treats
/// anything below 1.0 as "no answer" and returns the game's own 1.6 -- so a
/// build whose C# side never runs behaves exactly like an unwoven one. And if
/// the anchor below is ever not found, because the game was updated and the
/// method moved or the constants changed, nothing is written and the build
/// continues on stock behaviour. A missing frill must never cost somebody a
/// game that boots.
/// </summary>
internal static class Builtin
{
    /// The type injected into Assembly-CSharp. Deliberately in the global
    /// namespace, as the game's own classes are, and deliberately named so
    /// that it is obvious in a stack trace that it is not Team Cherry's.
    public const string GateType = "SilksongAspectGate";

    /// The field the game's C# writes, and the method the woven call reads it
    /// through. Named on the patch side too -- see AspectGate.cs.
    public const string FloorField = "Floor";
    public const string FloorMethod = "GetFloor";
    public const string CeilingField = "Ceiling";
    public const string CeilingMethod = "GetCeiling";

    /// What the game asks for, and what it means.
    ///
    /// The pair is the anchor rather than either number alone: 1.6f appears
    /// nine times in Assembly-CSharp and only once immediately before
    /// 2.3916667f. CameraAspectScaler builds a MinMaxFloat too, but from
    /// 1.7777778f, so matching the pair cannot hit it by accident -- and
    /// because BOTH ends are replaced from the one anchor, the ceiling is
    /// found positionally rather than by searching for its value, which is
    /// what keeps CameraAspectScaler's copy of it out of this.
    const float StockFloor = 1.6f;
    const float StockCeiling = 2.3916667f;

    const string TargetType = "ForceCameraAspect";
    const string TargetMethod = "AutoScaleViewportShared";

    /// <summary>
    /// Applies every built-in weave to the staged assembly set.
    ///
    /// Returns what happened, one line per weave, for the build log. Never
    /// throws: see the class comment.
    /// </summary>
    public static List<string> Apply(string assemblies)
    {
        var notes = new List<string>();

        // Controller names reported by Android can flicker during display
        // reconfiguration. Debounce that list before InControl is allowed to
        // detach and rebuild its live devices.
        WeaveInputRefreshGuard(assemblies, notes);

        var path = Path.Combine(assemblies, "Assembly-CSharp.dll");
        if (!File.Exists(path))
        {
            notes.Add("aspect floor: no Assembly-CSharp.dll in the staged set; skipped");
            return notes;
        }

        var resolver = new StagedResolver(assemblies);
        AssemblyDefinition assembly;
        try
        {
            assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
            {
                // In memory, so the file can be written back to the same path.
                InMemory = true,
                AssemblyResolver = resolver,
            });
        }
        catch (Exception e)
        {
            notes.Add($"aspect floor: cannot read Assembly-CSharp.dll ({e.GetType().Name}); skipped");
            return notes;
        }

        bool changed;
        try
        {
            changed = WeaveAspectFloor(assembly, notes);
        }
        catch (Exception e)
        {
            notes.Add($"aspect floor: not applied ({e.GetType().Name}: {e.Message})");
            return notes;
        }

        if (!changed) return notes;

        try
        {
            assembly.Write(path);
            notes.Add("aspect floor: Assembly-CSharp.dll rewritten");
        }
        catch (Exception e)
        {
            notes.Add($"aspect floor: could not write Assembly-CSharp.dll ({e.GetType().Name}: {e.Message})");
        }
        return notes;
    }

    /// <summary>
    /// Makes InControl's Unity joystick refresh tolerant of transient name-list
    /// changes. Some Android handhelds republish joystick names while changing
    /// display configuration. A one-sample change makes InControl detach a
    /// controller even when the hardware never disconnected, which can discard
    /// an edge such as a short jump press.
    ///
    /// This replaces only UnityInputDeviceManager.Update's *enumeration check*.
    /// Device control polling remains unchanged. A new joystick-name hash must
    /// appear on three consecutive one-second samples before RefreshDevices is
    /// called, so real connect/disconnect changes still take effect after about
    /// three seconds while one-off flickers are ignored.
    ///
    /// It is deliberately applied to whichever staged assembly actually owns
    /// InControl.UnityInputDeviceManager; Unity/package versions differ in where
    /// that type is compiled. Unknown versions are left untouched.
    /// </summary>
    static void WeaveInputRefreshGuard(string assemblies, List<string> notes)
    {
        var paths = Directory.GetFiles(assemblies, "*.dll", SearchOption.TopDirectoryOnly);
        Array.Sort(paths, (a, b) =>
        {
            int Rank(string p)
            {
                var n = Path.GetFileName(p);
                if (n == "InControl.dll") return 0;
                if (n == "Assembly-CSharp-firstpass.dll") return 1;
                if (n == "Assembly-CSharp.dll") return 2;
                return 3;
            }
            return Rank(a) != Rank(b) ? Rank(a).CompareTo(Rank(b)) :
                StringComparer.Ordinal.Compare(a, b);
        });

        foreach (var path in paths)
        {
            try
            {
                using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    InMemory = true,
                    AssemblyResolver = new StagedResolver(assemblies),
                });
                var module = assembly.MainModule;
                var manager = module.GetType("InControl.UnityInputDeviceManager");
                if (manager is null) continue;

                if (manager.Fields.Any(f => f.Name == "silksongRefreshCandidateHash"))
                {
                    notes.Add("controller refresh guard: already woven");
                    return;
                }

                var update = manager.Methods.FirstOrDefault(m =>
                    m.Name == "Update" && m.HasBody && !m.IsStatic &&
                    m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.FullName == "System.UInt64" &&
                    m.Parameters[1].ParameterType.FullName == "System.Single");
                var refresh = manager.Methods.FirstOrDefault(m =>
                    m.Name == "RefreshDevices" && m.HasBody && !m.IsStatic &&
                    m.Parameters.Count == 0);
                var joystickHash = manager.Fields.FirstOrDefault(f =>
                    f.Name == "joystickHash" && f.FieldType.FullName == "System.String");
                var inputType = module.GetTypeReferences().FirstOrDefault(t =>
                    t.FullName == "UnityEngine.Input");

                if (update is null || refresh is null || joystickHash is null || inputType is null)
                {
                    notes.Add("controller refresh guard: InControl shape differs; skipped");
                    return;
                }

                var candidate = new FieldDefinition(
                    "silksongRefreshCandidateHash",
                    FieldAttributes.Private,
                    module.TypeSystem.String);
                var stable = new FieldDefinition(
                    "silksongRefreshStableSamples",
                    FieldAttributes.Private,
                    module.TypeSystem.Int32);
                var timer = new FieldDefinition(
                    "silksongRefreshSampleTimer",
                    FieldAttributes.Private,
                    module.TypeSystem.Single);
                manager.Fields.Add(candidate);
                manager.Fields.Add(stable);
                manager.Fields.Add(timer);

                var getNames = new MethodReference(
                    "GetJoystickNames",
                    new ArrayType(module.TypeSystem.String),
                    inputType)
                {
                    HasThis = false,
                    CallingConvention = MethodCallingConvention.Default,
                };

                var join = new MethodReference(
                    "Join", module.TypeSystem.String, module.TypeSystem.String)
                {
                    HasThis = false,
                    CallingConvention = MethodCallingConvention.Default,
                };
                join.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                join.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.String)));

                var format = new MethodReference(
                    "Format", module.TypeSystem.String, module.TypeSystem.String)
                {
                    HasThis = false,
                    CallingConvention = MethodCallingConvention.Default,
                };
                format.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                format.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));
                format.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object));

                var equals = new MethodReference(
                    "op_Equality", module.TypeSystem.Boolean, module.TypeSystem.String)
                {
                    HasThis = false,
                    CallingConvention = MethodCallingConvention.Default,
                };
                equals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                equals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

                // The manager owns these fields, so this code is ordinary IL
                // access rather than reflection or a runtime detour.
                var body = update.Body;
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.InitLocals = true;
                var namesLocal = new VariableDefinition(new ArrayType(module.TypeSystem.String));
                var hashLocal = new VariableDefinition(module.TypeSystem.String);
                body.Variables.Add(namesLocal);
                body.Variables.Add(hashLocal);
                body.MaxStackSize = 4;

                var il = body.GetILProcessor();
                var done = il.Create(OpCodes.Ret);
                var sampleStable = il.Create(OpCodes.Nop);
                var countCheck = il.Create(OpCodes.Nop);

                // Sample no faster than once per second.
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Dup));
                il.Append(il.Create(OpCodes.Ldfld, timer));
                il.Append(il.Create(OpCodes.Ldarg_2));
                il.Append(il.Create(OpCodes.Add));
                il.Append(il.Create(OpCodes.Stfld, timer));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, timer));
                il.Append(il.Create(OpCodes.Ldc_R4, 1.0f));
                il.Append(il.Create(OpCodes.Blt, done));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldc_R4, 0.0f));
                il.Append(il.Create(OpCodes.Stfld, timer));

                // Use the same hash representation as InControl's original
                // property: "<count>: <comma-separated joystick names>".
                il.Append(il.Create(OpCodes.Call, getNames));
                il.Append(il.Create(OpCodes.Stloc, namesLocal));
                il.Append(il.Create(OpCodes.Ldstr, "{0}: {1}"));
                il.Append(il.Create(OpCodes.Ldloc, namesLocal));
                il.Append(il.Create(OpCodes.Ldlen));
                il.Append(il.Create(OpCodes.Conv_I4));
                il.Append(il.Create(OpCodes.Box, module.TypeSystem.Int32));
                il.Append(il.Create(OpCodes.Ldstr, ", "));
                il.Append(il.Create(OpCodes.Ldloc, namesLocal));
                il.Append(il.Create(OpCodes.Call, join));
                il.Append(il.Create(OpCodes.Call, format));
                il.Append(il.Create(OpCodes.Stloc, hashLocal));

                // A changed value starts a new stability window; matching
                // samples saturate at three so this counter never overflows.
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, candidate));
                il.Append(il.Create(OpCodes.Ldloc, hashLocal));
                il.Append(il.Create(OpCodes.Call, equals));
                il.Append(il.Create(OpCodes.Brtrue, sampleStable));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldloc, hashLocal));
                il.Append(il.Create(OpCodes.Stfld, candidate));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Stfld, stable));
                il.Append(il.Create(OpCodes.Br, countCheck));

                il.Append(sampleStable);
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, stable));
                il.Append(il.Create(OpCodes.Ldc_I4_3));
                il.Append(il.Create(OpCodes.Bge, countCheck));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, stable));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Add));
                il.Append(il.Create(OpCodes.Stfld, stable));

                il.Append(countCheck);
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, stable));
                il.Append(il.Create(OpCodes.Ldc_I4_3));
                il.Append(il.Create(OpCodes.Blt, done));

                // No refresh when the stable list is the one already attached.
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, joystickHash));
                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Ldfld, candidate));
                il.Append(il.Create(OpCodes.Call, equals));
                il.Append(il.Create(OpCodes.Brtrue, done));

                il.Append(il.Create(OpCodes.Ldarg_0));
                il.Append(il.Create(OpCodes.Call, refresh));
                il.Append(done);

                assembly.Write(path);
                notes.Add("controller refresh guard: debounced InControl joystick changes in " +
                          Path.GetFileName(path));
                return;
            }
            catch (Exception e)
            {
                notes.Add("controller refresh guard: skipped " + Path.GetFileName(path) +
                          " (" + e.GetType().Name + ")");
                return;
            }
        }

        notes.Add("controller refresh guard: InControl.UnityInputDeviceManager not found; skipped");
    }

    /// <summary>
    /// Replaces the floor constant with a call to an injected gate.
    ///
    /// One instruction changes. Everything else about the method -- the
    /// ceiling, the clamp, the viewport arithmetic, the height multiplier --
    /// is left exactly as Team Cherry wrote it, because the goal is to let the
    /// game's own code run on a number it was never given rather than to
    /// reimplement any part of it.
    /// </summary>
    static bool WeaveAspectFloor(AssemblyDefinition assembly, List<string> notes)
    {
        var module = assembly.MainModule;

        var type = module.GetType(TargetType);
        if (type is null)
        {
            notes.Add($"aspect floor: no {TargetType} in this build; skipped");
            return false;
        }

        var method = type.Methods.FirstOrDefault(m => m.Name == TargetMethod && m.HasBody);
        if (method is null)
        {
            notes.Add($"aspect floor: {TargetType}.{TargetMethod} not found; skipped");
            return false;
        }

        // The anchor: ldc.r4 1.6 immediately followed by ldc.r4 2.3916667.
        var il = method.Body.Instructions;
        Instruction? floor = null;
        for (var i = 0; i + 1 < il.Count; i++)
        {
            if (!IsLoad(il[i], StockFloor)) continue;
            if (!IsLoad(il[i + 1], StockCeiling)) continue;
            if (floor is not null)
            {
                notes.Add($"aspect floor: {TargetType}.{TargetMethod} has more than one "
                          + "candidate pair; skipped");
                return false;
            }
            floor = il[i];
        }

        if (floor is null)
        {
            // Already woven is not a failure: it is what a second run looks
            // like, and saying so is more useful than saying nothing.
            var already = module.GetType(GateType) is not null;
            notes.Add(already
                ? "aspect floor: already woven; nothing to do"
                : $"aspect floor: the {StockFloor}/{StockCeiling} pair is not in "
                  + $"{TargetType}.{TargetMethod}; skipped (game updated?)");
            return false;
        }

        // Taken before anything is replaced: the ceiling is the instruction
        // after the floor, and once the floor is swapped out that relationship
        // is gone.
        var ceiling = floor.Next;

        var gate = module.GetType(GateType) ?? InjectGate(module);
        var floorGetter = gate.Methods.First(m => m.Name == FloorMethod);
        var ceilingGetter = gate.Methods.First(m => m.Name == CeilingMethod);

        var processor = method.Body.GetILProcessor();
        processor.Replace(floor, processor.Create(OpCodes.Call, floorGetter));
        processor.Replace(ceiling, processor.Create(OpCodes.Call, ceilingGetter));

        notes.Add($"aspect range: {TargetType}.{TargetMethod} now asks {GateType} "
                  + $"instead of loading {StockFloor}/{StockCeiling}");
        return true;
    }

    static bool IsLoad(Instruction instruction, float value) =>
        instruction.OpCode == OpCodes.Ldc_R4
        && instruction.Operand is float f
        && f.Equals(value);

    /// <summary>
    /// Injects <c>public static class SilksongAspectGate</c>.
    ///
    /// <code>
    ///     public static float Floor;                  // 0 until the game sets it
    ///     public static float Ceiling;
    ///     public static float GetFloor()              // what the woven calls read
    ///         => Floor   &lt; 1f ? 1.6f       : Floor;
    ///     public static float GetCeiling()
    ///         => Ceiling &lt; 1f ? 2.3916667f : Ceiling;
    /// </code>
    ///
    /// There is deliberately no static constructor. Fields that default to
    /// zero and getters that treat zero as "nobody said" need no
    /// initialisation order to be correct, and cannot be defeated by the game
    /// reading the value before our C# has run -- which, since this is read
    /// from Update, it otherwise could be.
    /// </summary>
    static TypeDefinition InjectGate(ModuleDefinition module)
    {
        var type = new TypeDefinition(
            "", GateType,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
                | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            module.TypeSystem.Object);

        AddGate(module, type, FloorField, FloorMethod, StockFloor);
        AddGate(module, type, CeilingField, CeilingMethod, StockCeiling);

        module.Types.Add(type);
        return type;
    }

    /// <summary>One field and the getter that defends it.</summary>
    static void AddGate(
        ModuleDefinition module, TypeDefinition type,
        string fieldName, string methodName, float stock)
    {
        var single = module.TypeSystem.Single;

        var field = new FieldDefinition(
            fieldName, FieldAttributes.Public | FieldAttributes.Static, single);
        type.Fields.Add(field);

        var getter = new MethodDefinition(
            methodName,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            single);

        var il = getter.Body.GetILProcessor();
        var fallback = il.Create(OpCodes.Ldc_R4, stock);

        // Below 1.0 -- and, because blt.un is the unordered comparison, an
        // unset NaN too -- means nobody has answered; use the game's own. An
        // aspect ratio below 1.0 is a portrait window, which this must never
        // hand the game whatever it is asked for.
        il.Append(il.Create(OpCodes.Ldsfld, field));
        il.Append(il.Create(OpCodes.Ldc_R4, 1f));
        il.Append(il.Create(OpCodes.Blt_Un_S, fallback));
        il.Append(il.Create(OpCodes.Ldsfld, field));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(fallback);
        il.Append(il.Create(OpCodes.Ret));

        type.Methods.Add(getter);
    }
}
