// patch_swf.cs — Fix Mono WinForms HiDPI on Unix (two-DLL patcher)
//
// Root cause: libgdiplus renders text at 96 DPI regardless of Xft.dpi,
// but Font.Height and Graphics.DpiX DO respect Xft.dpi.  This causes:
//   1. AutoScaleMode.Font never triggers (MeasureText ≈ design-time)
//   2. Font.Height is 2-3x larger than actual rendered text
//   3. Layout based on Font.Height causes overlapping controls
//
// Fix (two patches):
//   A. System.Drawing.dll — Font.GetHeight() returns 96-DPI-based values
//      so layout matches actual text rendering.  Graphics.DpiX is NOT
//      affected, so KeePass DpiUtil still scales images by the real DPI.
//
//   B. System.Windows.Forms.dll — PerformAutoScale scales the form's
//      Font.Size by (actualDpi / 96) so text is physically readable.
//      The font change triggers OnFontChanged → PerformAutoScale →
//      controls scale to match via AutoScaleMode.Font.
//
//   C. System.Windows.Forms.dll — ToolStripManager.DefaultFont returns
//      a DPI-scaled font.  ToolStrip-based controls (MenuStrip,
//      StatusStrip, ContextMenuStrip) use this instead of inheriting
//      from the parent form's Font.

using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchSwf
{
    static int Main(string[] args)
    {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: mono patch_swf.exe <input_dir> <output_dir>");
            Console.Error.WriteLine("  Patches System.Windows.Forms.dll and System.Drawing.dll");
            Console.Error.WriteLine("  from input_dir into output_dir.");
            return 1;
        }

        string inDir = args[0];
        string outDir = args[1];

        int ret;
        ret = PatchSystemDrawing(inDir, outDir);
        if (ret != 0) return ret;
        ret = PatchSystemWindowsForms(inDir, outDir);
        if (ret != 0) return ret;

        Console.WriteLine("All patches applied successfully.");
        return 0;
    }

    // ================================================================
    // PATCH A: System.Drawing.dll — Font.GetHeight() uses 96 DPI
    // ================================================================
    static int PatchSystemDrawing(string inDir, string outDir)
    {
        string inputPath = System.IO.Path.Combine(inDir, "System.Drawing.dll");
        string outputPath = System.IO.Path.Combine(outDir, "System.Drawing.dll");

        if (!System.IO.File.Exists(inputPath)) {
            Console.Error.WriteLine("ERROR: " + inputPath + " not found");
            return 1;
        }

        Console.WriteLine("=== Patching System.Drawing.dll ===");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(inDir);
        resolver.AddSearchDirectory("/usr/lib/mono/4.5");

        var asm = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters {
            AssemblyResolver = resolver
        });
        var module = asm.MainModule;

        // Find Font.GetHeight() — the no-arg overload
        var fontType = module.Types.FirstOrDefault(t => t.FullName == "System.Drawing.Font");
        if (fontType == null) {
            Console.Error.WriteLine("ERROR: System.Drawing.Font not found");
            return 1;
        }

        var getHeightNoArg = fontType.Methods.FirstOrDefault(m =>
            m.Name == "GetHeight" && m.Parameters.Count == 0);
        var getHeightFloat = fontType.Methods.FirstOrDefault(m =>
            m.Name == "GetHeight" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.Single");

        if (getHeightNoArg == null || getHeightFloat == null) {
            Console.Error.WriteLine("ERROR: Font.GetHeight methods not found");
            return 1;
        }

        // Rewrite GetHeight() to: return GetHeight(96f);
        // Original: return GetHeight(Graphics.systemDpiY);
        Console.WriteLine("  Patching Font.GetHeight() to use 96 DPI...");
        {
            var body = getHeightNoArg.Body;
            body.Instructions.Clear();
            body.Variables.Clear();
            body.ExceptionHandlers.Clear();
            var il = body.GetILProcessor();

            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_R4, 96f));
            il.Append(il.Create(OpCodes.Call, getHeightFloat));
            il.Append(il.Create(OpCodes.Ret));
        }

        asm.Write(outputPath, new WriterParameters());
        Console.WriteLine("  Saved to: " + outputPath);
        return 0;
    }

    // ================================================================
    // PATCH B: System.Windows.Forms.dll — PerformAutoScale font scaling
    // ================================================================
    static int PatchSystemWindowsForms(string inDir, string outDir)
    {
        string inputPath = System.IO.Path.Combine(inDir, "System.Windows.Forms.dll");
        string outputPath = System.IO.Path.Combine(outDir, "System.Windows.Forms.dll");

        if (!System.IO.File.Exists(inputPath)) {
            Console.Error.WriteLine("ERROR: " + inputPath + " not found");
            return 1;
        }

        Console.WriteLine("=== Patching System.Windows.Forms.dll ===");

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(inDir);
        resolver.AddSearchDirectory(outDir); // patched System.Drawing
        resolver.AddSearchDirectory("/usr/lib/mono/4.5");

        var asm = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters {
            AssemblyResolver = resolver
        });
        var module = asm.MainModule;

        var ccType = module.Types.FirstOrDefault(t => t.FullName == "System.Windows.Forms.ContainerControl");
        var trType = module.Types.FirstOrDefault(t => t.FullName == "System.Windows.Forms.TextRenderer");
        var xuiType = module.Types.FirstOrDefault(t => t.FullName == "System.Windows.Forms.XplatUI");
        var ctrlType = module.Types.FirstOrDefault(t => t.FullName == "System.Windows.Forms.Control");

        var getDpi = trType?.Methods.FirstOrDefault(m => m.Name == "GetDpi");
        var runOnUnix = xuiType?.Methods.FirstOrDefault(m => m.Name == "get_RunningOnUnix");
        var getFont = ctrlType?.Methods.FirstOrDefault(m => m.Name == "get_Font");
        var setFont = ctrlType?.Methods.FirstOrDefault(m => m.Name == "set_Font");

        if (getDpi == null || runOnUnix == null || getFont == null || setFont == null) {
            Console.Error.WriteLine("ERROR: Required members not found in SWF");
            return 1;
        }

        var autoScaleMode = ccType.Fields.First(f => f.Name == "auto_scale_mode");

        var sizeFType = module.ImportReference(typeof(System.Drawing.SizeF)).Resolve();
        var sizeFGetH = module.ImportReference(
            sizeFType.Methods.First(m => m.Name == "get_Height" && !m.HasParameters));

        var fontType = module.ImportReference(typeof(System.Drawing.Font)).Resolve();
        var fontGetFamily = module.ImportReference(
            fontType.Methods.First(m => m.Name == "get_FontFamily" && !m.HasParameters));
        var fontGetSize = module.ImportReference(
            fontType.Methods.First(m => m.Name == "get_Size" && !m.HasParameters));
        var fontGetStyle = module.ImportReference(
            fontType.Methods.First(m => m.Name == "get_Style" && !m.HasParameters));
        var fontCtor = module.ImportReference(
            fontType.Methods.First(m => m.IsConstructor && m.Parameters.Count == 3 &&
                m.Parameters[0].ParameterType.FullName == "System.Drawing.FontFamily" &&
                m.Parameters[1].ParameterType.FullName == "System.Single" &&
                m.Parameters[2].ParameterType.FullName == "System.Drawing.FontStyle"));

        // Patch PerformAutoScale(bool)
        Console.WriteLine("  Patching PerformAutoScale(bool)...");
        var method = ccType.Methods.First(m =>
            m.Name == "PerformAutoScale" && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.FullName == "System.Boolean");
        var body = method.Body;
        var il = body.GetILProcessor();

        var lastRet = body.Instructions.Last(i => i.OpCode == OpCodes.Ret);

        var dpiLocal = new VariableDefinition(module.ImportReference(typeof(System.Drawing.SizeF)));
        var factorLocal = new VariableDefinition(module.ImportReference(typeof(float)));
        body.Variables.Add(dpiLocal);
        body.Variables.Add(factorLocal);

        var end = lastRet;

        // if (!RunningOnUnix) goto END
        il.InsertBefore(end, il.Create(OpCodes.Call, runOnUnix));
        il.InsertBefore(end, il.Create(OpCodes.Brfalse, end));

        // if (auto_scale_mode != Font) goto END
        il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(end, il.Create(OpCodes.Ldfld, autoScaleMode));
        il.InsertBefore(end, il.Create(OpCodes.Ldc_I4_1));
        il.InsertBefore(end, il.Create(OpCodes.Bne_Un, end));

        // SizeF dpi = TextRenderer.GetDpi()
        il.InsertBefore(end, il.Create(OpCodes.Call, getDpi));
        il.InsertBefore(end, il.Create(OpCodes.Stloc, dpiLocal));

        // float dpiFactor = dpi.Height / 96
        il.InsertBefore(end, il.Create(OpCodes.Ldloca, dpiLocal));
        il.InsertBefore(end, il.Create(OpCodes.Call, sizeFGetH));
        il.InsertBefore(end, il.Create(OpCodes.Ldc_R4, 96f));
        il.InsertBefore(end, il.Create(OpCodes.Div));
        il.InsertBefore(end, il.Create(OpCodes.Stloc, factorLocal));

        // if (dpiFactor <= 1.1) goto END
        il.InsertBefore(end, il.Create(OpCodes.Ldloc, factorLocal));
        il.InsertBefore(end, il.Create(OpCodes.Ldc_R4, 1.1f));
        il.InsertBefore(end, il.Create(OpCodes.Ble_Un, end));

        // if (Font.Size >= 13) goto END
        il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, getFont));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, fontGetSize));
        il.InsertBefore(end, il.Create(OpCodes.Ldc_R4, 13f));
        il.InsertBefore(end, il.Create(OpCodes.Bge_Un, end));

        // this.Font = new Font(Font.FontFamily, Font.Size * dpiFactor, Font.Style)
        il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));  // this for set_Font

        il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, getFont));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, fontGetFamily));

        il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, getFont));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, fontGetSize));
        il.InsertBefore(end, il.Create(OpCodes.Ldloc, factorLocal));
        il.InsertBefore(end, il.Create(OpCodes.Mul));

        il.InsertBefore(end, il.Create(OpCodes.Ldarg_0));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, getFont));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, fontGetStyle));

        il.InsertBefore(end, il.Create(OpCodes.Newobj, fontCtor));
        il.InsertBefore(end, il.Create(OpCodes.Callvirt, setFont));

        body.MaxStackSize = Math.Max(body.MaxStackSize, 6);

        // ============================================================
        // PATCH E: Control.DefaultFont — DPI-scaled default font
        // ============================================================
        // Original: return ThemeEngine.Current.DefaultFont;
        // Patched:  scale result by DPI factor on Unix.
        // This ensures KeePass's FontUtil.SetDefaultFont captures a
        // scaled font, so FontUtil.AssignDefault (called on ~47 controls)
        // sets the proper HiDPI size.
        Console.WriteLine("  Patching Control.get_DefaultFont...");

        var ctrlGetDefaultFont = ctrlType.Methods.First(m =>
            m.Name == "get_DefaultFont" && m.IsStatic);
        {
            var b = ctrlGetDefaultFont.Body;
            var origInstructions = b.Instructions.ToArray();
            b.Instructions.Clear();
            b.Variables.Clear();
            b.ExceptionHandlers.Clear();

            var fontLocal5 = new VariableDefinition(module.ImportReference(typeof(System.Drawing.Font)));
            var dpiLocal5 = new VariableDefinition(module.ImportReference(typeof(System.Drawing.SizeF)));
            var facLocal5 = new VariableDefinition(module.ImportReference(typeof(float)));
            b.Variables.Add(fontLocal5);
            b.Variables.Add(dpiLocal5);
            b.Variables.Add(facLocal5);

            var p = b.GetILProcessor();

            // Reproduce original: ThemeEngine.Current.DefaultFont
            foreach (var orig in origInstructions) {
                if (orig.OpCode == OpCodes.Ret) break;
                p.Append(orig);
            }
            p.Append(p.Create(OpCodes.Stloc, fontLocal5));

            var retOrig5 = p.Create(OpCodes.Ldloc, fontLocal5);

            // if (!RunningOnUnix) goto ret
            p.Append(p.Create(OpCodes.Call, runOnUnix));
            p.Append(p.Create(OpCodes.Brfalse, retOrig5));

            // if (font == null) goto ret
            p.Append(p.Create(OpCodes.Ldloc, fontLocal5));
            p.Append(p.Create(OpCodes.Brfalse, retOrig5));

            // if (font.Size >= 13) goto ret (already scaled)
            p.Append(p.Create(OpCodes.Ldloc, fontLocal5));
            p.Append(p.Create(OpCodes.Callvirt, fontGetSize));
            p.Append(p.Create(OpCodes.Ldc_R4, 13f));
            p.Append(p.Create(OpCodes.Bge_Un, retOrig5));

            // SizeF dpi = TextRenderer.GetDpi()
            p.Append(p.Create(OpCodes.Call, getDpi));
            p.Append(p.Create(OpCodes.Stloc, dpiLocal5));

            // float factor = dpi.Height / 96
            p.Append(p.Create(OpCodes.Ldloca, dpiLocal5));
            p.Append(p.Create(OpCodes.Call, sizeFGetH));
            p.Append(p.Create(OpCodes.Ldc_R4, 96f));
            p.Append(p.Create(OpCodes.Div));
            p.Append(p.Create(OpCodes.Stloc, facLocal5));

            // if (factor <= 1.1) goto ret
            p.Append(p.Create(OpCodes.Ldloc, facLocal5));
            p.Append(p.Create(OpCodes.Ldc_R4, 1.1f));
            p.Append(p.Create(OpCodes.Ble_Un, retOrig5));

            // return new Font(f.FontFamily, f.Size * factor, f.Style)
            p.Append(p.Create(OpCodes.Ldloc, fontLocal5));
            p.Append(p.Create(OpCodes.Callvirt, fontGetFamily));
            p.Append(p.Create(OpCodes.Ldloc, fontLocal5));
            p.Append(p.Create(OpCodes.Callvirt, fontGetSize));
            p.Append(p.Create(OpCodes.Ldloc, facLocal5));
            p.Append(p.Create(OpCodes.Mul));
            p.Append(p.Create(OpCodes.Ldloc, fontLocal5));
            p.Append(p.Create(OpCodes.Callvirt, fontGetStyle));
            p.Append(p.Create(OpCodes.Newobj, fontCtor));
            p.Append(p.Create(OpCodes.Ret));

            // retOrig: return f;
            p.Append(retOrig5);
            p.Append(p.Create(OpCodes.Ret));

            b.MaxStackSize = 4;
        }

        // ============================================================
        // PATCH C: ToolStripManager.DefaultFont — DPI-scaled font
        // ============================================================
        // Original: return SystemFonts.MessageBoxFont;
        // Patched:  var f = SystemFonts.MessageBoxFont;
        //           if (RunningOnUnix) {
        //               float dpi = TextRenderer.GetDpi().Height;
        //               float factor = dpi / 96f;
        //               if (factor > 1.1f)
        //                   f = new Font(f.FontFamily, f.Size * factor, f.Style);
        //           }
        //           return f;
        Console.WriteLine("  Patching ToolStripManager.DefaultFont...");

        var tsmType = module.Types.FirstOrDefault(t => t.FullName == "System.Windows.Forms.ToolStripManager");
        var tsmGetDefaultFont = tsmType.Methods.First(m => m.Name == "get_DefaultFont");
        {
            var b = tsmGetDefaultFont.Body;
            b.Instructions.Clear();
            b.Variables.Clear();
            b.ExceptionHandlers.Clear();

            var fontLocal = new VariableDefinition(module.ImportReference(typeof(System.Drawing.Font)));
            var dpiSizeLocal = new VariableDefinition(module.ImportReference(typeof(System.Drawing.SizeF)));
            var factorLocal2 = new VariableDefinition(module.ImportReference(typeof(float)));
            b.Variables.Add(fontLocal);
            b.Variables.Add(dpiSizeLocal);
            b.Variables.Add(factorLocal2);

            var p = b.GetILProcessor();

            // SystemFonts.MessageBoxFont
            var sysFontsType = module.ImportReference(typeof(System.Drawing.SystemFonts)).Resolve();
            var getMBFont = module.ImportReference(
                sysFontsType.Methods.First(m => m.Name == "get_MessageBoxFont"));

            // var f = SystemFonts.MessageBoxFont;
            p.Append(p.Create(OpCodes.Call, getMBFont));
            p.Append(p.Create(OpCodes.Stloc, fontLocal));

            // if (!RunningOnUnix) goto retLabel
            var retInstr = p.Create(OpCodes.Ldloc, fontLocal);
            p.Append(p.Create(OpCodes.Call, runOnUnix));
            p.Append(p.Create(OpCodes.Brfalse, retInstr));

            // SizeF dpi = TextRenderer.GetDpi()
            p.Append(p.Create(OpCodes.Call, getDpi));
            p.Append(p.Create(OpCodes.Stloc, dpiSizeLocal));

            // float factor = dpi.Height / 96f
            p.Append(p.Create(OpCodes.Ldloca, dpiSizeLocal));
            p.Append(p.Create(OpCodes.Call, sizeFGetH));
            p.Append(p.Create(OpCodes.Ldc_R4, 96f));
            p.Append(p.Create(OpCodes.Div));
            p.Append(p.Create(OpCodes.Stloc, factorLocal2));

            // if (factor <= 1.1f) goto retLabel
            p.Append(p.Create(OpCodes.Ldloc, factorLocal2));
            p.Append(p.Create(OpCodes.Ldc_R4, 1.1f));
            p.Append(p.Create(OpCodes.Ble_Un, retInstr));

            // f = new Font(f.FontFamily, f.Size * factor, f.Style)
            p.Append(p.Create(OpCodes.Ldloc, fontLocal));
            p.Append(p.Create(OpCodes.Callvirt, fontGetFamily));

            p.Append(p.Create(OpCodes.Ldloc, fontLocal));
            p.Append(p.Create(OpCodes.Callvirt, fontGetSize));
            p.Append(p.Create(OpCodes.Ldloc, factorLocal2));
            p.Append(p.Create(OpCodes.Mul));

            p.Append(p.Create(OpCodes.Ldloc, fontLocal));
            p.Append(p.Create(OpCodes.Callvirt, fontGetStyle));

            p.Append(p.Create(OpCodes.Newobj, fontCtor));
            p.Append(p.Create(OpCodes.Stloc, fontLocal));

            // return f;
            p.Append(retInstr);  // ldloc fontLocal
            p.Append(p.Create(OpCodes.Ret));

            b.MaxStackSize = 4;
        }

        // ============================================================
        // PATCH D: ToolStrip.ImageScalingSize — DPI-scaled icons
        // ============================================================
        // Original: return image_scaling_size;
        // Patched:  if on Unix and size is 16x16 (default), scale by DPI
        Console.WriteLine("  Patching ToolStrip.get_ImageScalingSize...");

        var tsType = module.Types.FirstOrDefault(t => t.FullName == "System.Windows.Forms.ToolStrip");
        var tsGetISS = tsType.Methods.First(m => m.Name == "get_ImageScalingSize");
        var issField = tsType.Fields.First(f => f.Name == "image_scaling_size");
        {
            var b = tsGetISS.Body;
            b.Instructions.Clear();
            b.Variables.Clear();
            b.ExceptionHandlers.Clear();

            var sizeLocal = new VariableDefinition(module.ImportReference(typeof(System.Drawing.Size)));
            var dpiLocal3 = new VariableDefinition(module.ImportReference(typeof(System.Drawing.SizeF)));
            var facLocal3 = new VariableDefinition(module.ImportReference(typeof(float)));
            b.Variables.Add(sizeLocal);
            b.Variables.Add(dpiLocal3);
            b.Variables.Add(facLocal3);

            var sizeType = module.ImportReference(typeof(System.Drawing.Size)).Resolve();
            var sizeGetW = module.ImportReference(
                sizeType.Methods.First(m => m.Name == "get_Width" && !m.HasParameters));
            var sizeGetH = module.ImportReference(
                sizeType.Methods.First(m => m.Name == "get_Height" && !m.HasParameters));
            var sizeCtor = module.ImportReference(
                sizeType.Methods.First(m => m.IsConstructor && m.Parameters.Count == 2 &&
                    m.Parameters[0].ParameterType.FullName == "System.Int32"));

            var mathRound = module.ImportReference(
                typeof(Math).GetMethod("Round", new[] { typeof(double) }));
            var convR8 = OpCodes.Conv_R8;

            var p = b.GetILProcessor();

            // var s = this.image_scaling_size;
            p.Append(p.Create(OpCodes.Ldarg_0));
            p.Append(p.Create(OpCodes.Ldfld, issField));
            p.Append(p.Create(OpCodes.Stloc, sizeLocal));

            // if (!RunningOnUnix) goto retOrig
            var retOrig = p.Create(OpCodes.Ldloc, sizeLocal);
            p.Append(p.Create(OpCodes.Call, runOnUnix));
            p.Append(p.Create(OpCodes.Brfalse, retOrig));

            // if (s.Width != 16) goto retOrig
            p.Append(p.Create(OpCodes.Ldloca, sizeLocal));
            p.Append(p.Create(OpCodes.Call, sizeGetW));
            p.Append(p.Create(OpCodes.Ldc_I4_S, (sbyte)16));
            p.Append(p.Create(OpCodes.Bne_Un, retOrig));

            // if (s.Height != 16) goto retOrig
            p.Append(p.Create(OpCodes.Ldloca, sizeLocal));
            p.Append(p.Create(OpCodes.Call, sizeGetH));
            p.Append(p.Create(OpCodes.Ldc_I4_S, (sbyte)16));
            p.Append(p.Create(OpCodes.Bne_Un, retOrig));

            // SizeF dpi = TextRenderer.GetDpi()
            p.Append(p.Create(OpCodes.Call, getDpi));
            p.Append(p.Create(OpCodes.Stloc, dpiLocal3));

            // float factor = dpi.Height / 96f
            p.Append(p.Create(OpCodes.Ldloca, dpiLocal3));
            p.Append(p.Create(OpCodes.Call, sizeFGetH));
            p.Append(p.Create(OpCodes.Ldc_R4, 96f));
            p.Append(p.Create(OpCodes.Div));
            p.Append(p.Create(OpCodes.Stloc, facLocal3));

            // if (factor <= 1.1) goto retOrig
            p.Append(p.Create(OpCodes.Ldloc, facLocal3));
            p.Append(p.Create(OpCodes.Ldc_R4, 1.1f));
            p.Append(p.Create(OpCodes.Ble_Un, retOrig));

            // return new Size((int)Math.Round(16 * factor), (int)Math.Round(16 * factor))
            p.Append(p.Create(OpCodes.Ldc_R4, 16f));
            p.Append(p.Create(OpCodes.Ldloc, facLocal3));
            p.Append(p.Create(OpCodes.Mul));
            p.Append(p.Create(convR8));
            p.Append(p.Create(OpCodes.Call, mathRound));
            p.Append(p.Create(OpCodes.Conv_I4));

            p.Append(p.Create(OpCodes.Ldc_R4, 16f));
            p.Append(p.Create(OpCodes.Ldloc, facLocal3));
            p.Append(p.Create(OpCodes.Mul));
            p.Append(p.Create(convR8));
            p.Append(p.Create(OpCodes.Call, mathRound));
            p.Append(p.Create(OpCodes.Conv_I4));

            p.Append(p.Create(OpCodes.Newobj, sizeCtor));
            p.Append(p.Create(OpCodes.Ret));

            // retOrig: return s;
            p.Append(retOrig);
            p.Append(p.Create(OpCodes.Ret));

            b.MaxStackSize = 4;
        }

        // ============================================================
        // PATCH F: RichTextBox RTF font size — DPI-scaled during parse
        // ============================================================
        // When the RTF parser encounters \fsNN, it stores NN/2 (points)
        // in rtf_style.rtf_rtffont_size.  We multiply by DPI/96 so
        // the font renders at the correct physical size on HiDPI.
        Console.WriteLine("  Patching RichTextBox HandleControl (RTF font size)...");

        var rtbType = module.Types.FirstOrDefault(t => t.FullName == "System.Windows.Forms.RichTextBox");
        var handleControl = rtbType.Methods.First(m =>
            m.Name == "HandleControl" && m.Parameters.Count == 1);

        var rtfSectionStyleType = rtbType.NestedTypes.First(t => t.Name == "RtfSectionStyle");
        var rtfFontSizeField = rtfSectionStyleType.Fields.First(f => f.Name == "rtf_rtffont_size");
        {
            var b = handleControl.Body;
            var instrs = b.Instructions;

            // Find: ldc.r4 2.0, div, stfld rtf_rtffont_size
            Instruction divInstr = null;
            for (int i = 0; i < instrs.Count - 2; i++) {
                if (instrs[i].OpCode == OpCodes.Ldc_R4 &&
                    Math.Abs((float)instrs[i].Operand - 2.0f) < 0.001f &&
                    instrs[i + 1].OpCode == OpCodes.Div &&
                    instrs[i + 2].OpCode == OpCodes.Stfld &&
                    ((FieldReference)instrs[i + 2].Operand).Name == "rtf_rtffont_size") {
                    divInstr = instrs[i + 1];
                    break;
                }
            }
            if (divInstr == null) {
                Console.Error.WriteLine("ERROR: RTF font size (param/2.0) not found in HandleControl");
                return 1;
            }

            // Insert after div, before stfld: multiply by DPI/96
            // Stack state after div: [rtf_style_ref, fontsize_float]
            var dpiLocalF = new VariableDefinition(module.ImportReference(typeof(System.Drawing.SizeF)));
            b.Variables.Add(dpiLocalF);

            var p = b.GetILProcessor();
            var stfldInstr = divInstr.Next; // stfld rtf_rtffont_size

            p.InsertBefore(stfldInstr, p.Create(OpCodes.Call, getDpi));
            p.InsertBefore(stfldInstr, p.Create(OpCodes.Stloc, dpiLocalF));
            p.InsertBefore(stfldInstr, p.Create(OpCodes.Ldloca, dpiLocalF));
            p.InsertBefore(stfldInstr, p.Create(OpCodes.Call, sizeFGetH));
            p.InsertBefore(stfldInstr, p.Create(OpCodes.Ldc_R4, 96f));
            p.InsertBefore(stfldInstr, p.Create(OpCodes.Div));
            p.InsertBefore(stfldInstr, p.Create(OpCodes.Mul));

            b.MaxStackSize = Math.Max(b.MaxStackSize, 6);
        }

        // ============================================================
        // PATCH G: TabControlPainter.DrawTab — fix dark theme tab text
        // ============================================================
        // Two issues:
        // 1. DrawTab uses SystemBrushes.ControlText (black) instead of
        //    page.ForeColor for tab text → black on dark background.
        // 2. When DrawMode == OwnerDrawFixed (set by KeeTheme), DrawTab
        //    skips ALL text drawing and delegates to OnDrawItemInternal.
        //    KeeTheme's NativeWindow paint handler doesn't work on Mono,
        //    so nobody draws the text → invisible tabs.
        // Fix: replace SystemBrushes.ControlText with page.ForeColor,
        //      AND on Unix, skip the DrawMode check so text is always drawn.
        Console.WriteLine("  Patching TabControlPainter.DrawTab (tab text color)...");

        var tabPainterType = module.Types.FirstOrDefault(t =>
            t.FullName == "System.Windows.Forms.Theming.Default.TabControlPainter");
        var drawTab = tabPainterType.Methods.First(m =>
            m.Name == "DrawTab" && m.Parameters.Count == 5);
        {
            var b = drawTab.Body;
            var instrs = b.Instructions;

            var solidBrushType = module.ImportReference(typeof(System.Drawing.SolidBrush)).Resolve();
            var solidBrushCtor = module.ImportReference(
                solidBrushType.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1 &&
                    m.Parameters[0].ParameterType.FullName == "System.Drawing.Color"));

            var controlGetForeColor = module.ImportReference(
                ctrlType.Methods.First(m => m.Name == "get_ForeColor" && !m.HasParameters));

            var p = b.GetILProcessor();

            // Part 1: On Unix, neutralize the DrawMode check that skips text drawing.
            // Pattern: callvirt TabControl::get_DrawMode() / brtrue <skip>
            // Replace brtrue with pop (drop the DrawMode value, never branch).
            int drawModeFixed = 0;
            for (int i = 0; i < instrs.Count - 1; i++) {
                if (instrs[i].OpCode == OpCodes.Callvirt &&
                    instrs[i].Operand is MethodReference dm &&
                    dm.Name == "get_DrawMode" &&
                    dm.DeclaringType.Name == "TabControl" &&
                    instrs[i + 1].OpCode == OpCodes.Brtrue) {

                    // On Unix: replace "callvirt get_DrawMode; brtrue skip"
                    // with "pop; nop" — the ldarg.3 before pushes tab ref,
                    // pop discards it, nop fills the brtrue slot.
                    // Actually ldarg.3 is the instruction before instrs[i].
                    // callvirt get_DrawMode consumes the ref and pushes int.
                    // brtrue consumes the int.
                    // We want to: not call get_DrawMode, not branch.
                    // Replace callvirt with pop (consumes the tab ref from ldarg.3)
                    // Replace brtrue with nop

                    var callInstr = instrs[i];
                    var brInstr = instrs[i + 1];

                    // Wrap in RunningOnUnix check:
                    // if (RunningOnUnix) { pop; nop; } else { callvirt get_DrawMode; brtrue; }
                    // This is complex in IL. Simpler: just NOP both unconditionally.
                    // The only effect is that OwnerDraw tabs will draw text via Mono's
                    // default path instead of delegating. On Windows .NET, this code
                    // is never used (different Theme engine), so it's safe.

                    p.Replace(callInstr, p.Create(OpCodes.Pop));
                    p.Replace(brInstr, p.Create(OpCodes.Nop));
                    drawModeFixed++;
                    break;
                }
            }
            Console.WriteLine("    DrawMode skip branches neutralized: {0}", drawModeFixed);

            // Part 2: Replace SystemBrushes.ControlText with page.ForeColor
            int colorFixed = 0;
            for (int i = 0; i < instrs.Count; i++) {
                if (instrs[i].OpCode == OpCodes.Call &&
                    instrs[i].Operand is MethodReference mr &&
                    mr.Name == "get_ControlText" &&
                    mr.DeclaringType.FullName == "System.Drawing.SystemBrushes") {

                    var target = instrs[i];
                    p.InsertBefore(target, p.Create(OpCodes.Ldarg_2));
                    p.InsertBefore(target, p.Create(OpCodes.Callvirt, controlGetForeColor));
                    p.InsertBefore(target, p.Create(OpCodes.Newobj, solidBrushCtor));
                    p.Remove(target);
                    i += 2;
                    colorFixed++;
                }
            }

            Console.WriteLine("    SystemBrushes.ControlText → page.ForeColor: {0}", colorFixed);
            b.MaxStackSize = Math.Max(b.MaxStackSize, 4);
        }

        asm.Write(outputPath, new WriterParameters());
        Console.WriteLine("  Saved to: " + outputPath);
        return 0;
    }
}
