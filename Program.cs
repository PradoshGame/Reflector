using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;
using Raylib_cs;
using rlImGui_cs;

static unsafe class Program
{
    const string VS = @"#version 330
in vec3 vertexPosition;
in vec3 vertexNormal;
uniform mat4 mvp;
uniform mat4 matModel;
out vec3 fragPos;
out vec3 fragNormal;
out vec3 objPos;
void main()
{
    vec4 w = matModel * vec4(vertexPosition, 1.0);
    fragPos = w.xyz;
    fragNormal = normalize(mat3(matModel) * vertexNormal);
    objPos = vertexPosition;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    // Procedural ""studio"" environment reflected on the sphere = metallic look
    const string FS = @"#version 330
in vec3 fragPos;
in vec3 fragNormal;
in vec3 objPos;
uniform vec4 colDiffuse;
uniform vec3 viewPos;
out vec4 finalColor;

vec3 env(vec3 d)
{
    float h = d.y;
    vec3 sky    = mix(vec3(0.70, 0.82, 1.00), vec3(0.05, 0.15, 0.50), clamp(h, 0.0, 1.0));
    vec3 ground = mix(vec3(0.30, 0.26, 0.22), vec3(0.02, 0.02, 0.03), clamp(-h * 1.5, 0.0, 1.0));
    vec3 c = mix(ground, sky, smoothstep(-0.03, 0.03, h));
    c += vec3(1.0) * exp(-abs(h) * 16.0) * 0.7;                       // horizon glow
    vec3 sun = normalize(vec3(0.5, 0.7, 0.4));
    c += vec3(1.0, 0.92, 0.75) * pow(max(dot(d, sun), 0.0), 200.0) * 8.0; // sun
    vec3 box = normalize(vec3(-0.6, 0.45, -0.55));
    c += vec3(1.0) * smoothstep(0.90, 0.94, dot(d, box)) * 2.5;       // softbox
    return c;
}

void main()
{
    vec3 N = normalize(fragNormal);
    vec3 V = normalize(viewPos - fragPos);
    vec3 R = reflect(-V, N);

    vec3 F0 = colDiffuse.rgb;
    float ndv = max(dot(N, V), 0.0);
    vec3 fresnel = F0 + (1.0 - F0) * pow(1.0 - ndv, 5.0);

    vec3 col = env(R) * fresnel + F0 * 0.03;

    // engraved grooves in object space so you can actually SEE the spin
    vec3 p = normalize(objPos);
    float lat = abs(fract(p.y * 3.0 + 0.5) - 0.5);
    float lon = abs(fract(atan(p.z, p.x) / 6.2831853 * 12.0) - 0.5);
    float g = 1.0 - smoothstep(0.0, 0.035, min(lat, lon * 0.6));
    col *= mix(1.0, 0.2, g);

    col = col / (1.0 + col);          // tonemap
    col = pow(col, vec3(1.0 / 2.2));  // gamma
    finalColor = vec4(col, 1.0);
}";

    static Model BuildSphere(Shader shader, int seg)
    {
        Model m = Raylib.LoadModelFromMesh(Raylib.GenMeshSphere(1f, seg, seg));
        m.Materials[0].Shader = shader;
        return m;
    }

    // ---------------- GPU selection ----------------
    // An OpenGL context is bound to one GPU at creation time (InitWindow), so the
    // GPU can't be switched live. Picking one relaunches this app with the right
    // PRIME-offload environment variables set.

    record Gpu(string Name, string Bus);

    [DllImport("libGL.so.1")]
    static extern IntPtr glGetString(uint name);

    static string GlRenderer()
    {
        try
        {
            IntPtr p = glGetString(0x1F01); // GL_RENDERER
            return Marshal.PtrToStringAnsi(p) ?? "unknown";
        }
        catch { return "unknown"; }
    }

    static List<Gpu> DetectGpus()
    {
        var list = new List<Gpu>();
        try
        {
            var psi = new ProcessStartInfo("lspci") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (string line in text.Split('\n'))
            {
                if (!(line.Contains("VGA compatible") || line.Contains("3D controller") || line.Contains("Display controller")))
                    continue;
                int sp = line.IndexOf(' ');
                int colon = line.IndexOf(": ");
                if (sp < 0 || colon < 0) continue;
                list.Add(new Gpu(line[(colon + 2)..].Trim(), line[..sp]));
            }
        }
        catch { /* lspci missing: sudo pacman -S pciutils */ }
        return list;
    }

    static void Relaunch(int sel, List<Gpu> gpus)
    {
        string exe = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };

        // running through `dotnet run` / `dotnet x.dll`: host is "dotnet", so pass the dll
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);

        psi.ArgumentList.Add("--gpu");
        psi.ArgumentList.Add(sel.ToString());

        // clear anything inherited from a previous selection
        psi.Environment.Remove("DRI_PRIME");
        psi.Environment.Remove("__NV_PRIME_RENDER_OFFLOAD");
        psi.Environment.Remove("__GLX_VENDOR_LIBRARY_NAME");

        if (sel > 0 && sel - 1 < gpus.Count)
        {
            Gpu g = gpus[sel - 1];
            if (g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            {
                psi.Environment["__NV_PRIME_RENDER_OFFLOAD"] = "1";
                psi.Environment["__GLX_VENDOR_LIBRARY_NAME"] = "nvidia";
            }
            else
            {
                string bus = g.Bus.Count(c => c == ':') == 1 ? "0000:" + g.Bus : g.Bus;
                psi.Environment["DRI_PRIME"] = "pci-" + bus.Replace(':', '_').Replace('.', '_');
            }
        }
        Process.Start(psi);
    }

    static void Main(string[] args)
    {
        int activeGpu = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--gpu") int.TryParse(args[i + 1], out activeGpu);

        List<Gpu> gpus = DetectGpus();
        string[] gpuItems = new[] { "System default" }.Concat(gpus.Select(g => g.Name)).ToArray();
        int gpuSel = activeGpu;
        bool quit = false;

        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint | ConfigFlags.ResizableWindow);
        Raylib.InitWindow(1280, 720, "Reflector by Pradosh");
        Raylib.SetTargetFPS(0); // no cap
        rlImGui.Setup(true);
        string renderer = GlRenderer();

        Shader shader = Raylib.LoadShaderFromMemory(VS, FS);
        int viewLoc = Raylib.GetShaderLocation(shader, "viewPos");

        Model[] models =
        {
            BuildSphere(shader, 16),
            BuildSphere(shader, 32),
            BuildSphere(shader, 64),
        };

        Color[] palette =
        {
            new Color(255, 199, 87, 255),   // gold
            new Color(235, 235, 245, 255),  // silver
            new Color(245, 163, 138, 255),  // copper
            new Color(140, 155, 180, 255),  // steel
        };

        // settings
        bool vsync = false;
        int fpsCap = 0;
        int quality = 1;
        int rings = 3;
        int perRing = 6;
        float spinSpeed = 120f;
        float orbitSpeed = 25f;
        bool paused = false;

        // state
        float spin = 0f, orbit = 0f;
        float yaw = 0.6f, pitch = 0.35f, dist = 16f;
        float[] hist = new float[120];
        int histPos = 0;

        Camera3D cam = new Camera3D
        {
            Target = Vector3.Zero,
            Up = Vector3.UnitY,
            FovY = 45f,
            Projection = CameraProjection.Perspective,
        };

        while (!Raylib.WindowShouldClose() && !quit)
        {
            float dt = Raylib.GetFrameTime();

            // ---- input ----
            if (!ImGui.GetIO().WantCaptureMouse)
            {
                if (Raylib.IsMouseButtonDown(MouseButton.Left))
                {
                    Vector2 d = Raylib.GetMouseDelta();
                    yaw -= d.X * 0.005f;
                    pitch = Math.Clamp(pitch + d.Y * 0.005f, -1.4f, 1.4f);
                }
                dist = Math.Clamp(dist - Raylib.GetMouseWheelMove() * 0.8f, 4f, 40f);
            }
            if (Raylib.IsKeyPressed(KeyboardKey.F11)) Raylib.ToggleBorderlessWindowed();
            if (Raylib.IsKeyPressed(KeyboardKey.Space)) paused = !paused;

            if (!paused)
            {
                spin = (spin + dt * spinSpeed) % 36000f;
                orbit = (orbit + dt * orbitSpeed) % 36000f;
            }

            cam.Position = new Vector3(
                MathF.Cos(pitch) * MathF.Sin(yaw),
                MathF.Sin(pitch),
                MathF.Cos(pitch) * MathF.Cos(yaw)) * dist;

            hist[histPos] = dt * 1000f;
            histPos = (histPos + 1) % hist.Length;

            // ---- draw ----
            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(10, 12, 18, 255));

            Raylib.SetShaderValue(shader, viewLoc, cam.Position, ShaderUniformDataType.Vec3);
            Model model = models[quality];
            Vector3 tilt = new Vector3(0.25f, 1f, 0.15f);

            Raylib.BeginMode3D(cam);

            // centre sphere
            Raylib.DrawModelEx(model, Vector3.Zero, tilt, spin * 0.5f, new Vector3(1.6f), palette[0]);

            for (int r = 0; r < rings; r++)
            {
                float radius = 3.6f + r * 2.2f;
                int count = perRing + r * 3;
                float dir = (r % 2 == 0) ? 1f : -1f;
                float size = 0.55f + r * 0.08f;

                for (int i = 0; i < count; i++)
                {
                    float a = orbit * dir / (1f + r * 0.5f) + i * 360f / count;
                    float rad = a * MathF.PI / 180f;
                    Vector3 pos = new Vector3(
                        MathF.Cos(rad) * radius,
                        0.5f * MathF.Sin(rad * 2f + r),
                        MathF.Sin(rad) * radius);

                    Raylib.DrawModelEx(model, pos, tilt, spin * dir * 2f + i * 30f,
                        new Vector3(size), palette[(i + r) % palette.Length]);
                }
            }

            Raylib.EndMode3D();

            // ---- UI ----
            rlImGui.Begin();
            ImGui.SetNextWindowPos(new Vector2(12, 12), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
            ImGui.Begin("Reflector by Pradosh", ImGuiWindowFlags.AlwaysAutoResize);

            ImGui.TextWrapped($"GPU: {renderer}");
            ImGui.Combo("Select GPU", ref gpuSel, gpuItems, gpuItems.Length);
            if (gpuSel != activeGpu && ImGui.Button("Apply GPU (restarts app)"))
            {
                Relaunch(gpuSel, gpus);
                quit = true;
            }
            ImGui.Separator();

            ImGui.Text($"{Raylib.GetFPS()} FPS   {dt * 1000f:F3} ms");
            ImGui.PlotLines("##ft", ref hist[0], hist.Length, histPos, "frame ms", 0f, 20f, new Vector2(0, 50));
            ImGui.Separator();

            if (ImGui.Checkbox("VSync", ref vsync))
            {
                if (vsync) Raylib.SetWindowState(ConfigFlags.VSyncHint);
                else Raylib.ClearWindowState(ConfigFlags.VSyncHint);
            }
            if (ImGui.SliderInt("FPS cap (0 = off)", ref fpsCap, 0, 1000))
                Raylib.SetTargetFPS(fpsCap);

            ImGui.Combo("Sphere quality", ref quality, "Low\0Medium\0High\0");
            ImGui.SliderInt("Rings", ref rings, 0, 6);
            ImGui.SliderInt("Spheres / ring", ref perRing, 1, 24);
            ImGui.SliderFloat("Spin speed", ref spinSpeed, -720f, 720f);
            ImGui.SliderFloat("Orbit speed", ref orbitSpeed, -180f, 180f);
            ImGui.Checkbox("Pause (Space)", ref paused);

            if (ImGui.Button("Borderless fullscreen (F11)")) Raylib.ToggleBorderlessWindowed();

            int total = 1;
            for (int r = 0; r < rings; r++) total += perRing + r * 3;
            ImGui.Separator();
            ImGui.TextDisabled($"{total} spheres | LMB drag = orbit cam | wheel = zoom");

            ImGui.End();
            rlImGui.End();

            Raylib.EndDrawing();
        }

        rlImGui.Shutdown();
        Raylib.UnloadShader(shader);
        Raylib.CloseWindow();
    }
}