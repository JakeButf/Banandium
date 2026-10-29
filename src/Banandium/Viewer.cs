using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Numerics;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace Banandium;
public sealed partial class Viewer : GameWindow
{
    private ImGuiController ui = null !;
    private SceneRenderer renderer = null !;
    private ModelRenderer modelRenderer = null!;
    private SceneModels? models;
    private Task<SceneModels>? modelLoading;
    private bool modelsVisible = true;
    private Dump? dump;
    private SceneData? scene;
    private Task<Dump>? indexing;
    private Task<SceneData>? loading;
    private readonly string[] arguments;
    private string pendingDumpPath = "";
    private string pendingSceneName = "";
    private string startupScene = "";
    private string selectedScene = "";
    private string sceneFilter = "";
    private bool displayCommonNames = true;
    private string errorMessage = "";
    private bool openSceneDialog;
    private bool sceneDialogVisible;
    private bool openErrorDialog;
    private bool aboutVisible;
    private bool updatesVisible;
    private Task<UpdateResult>? updateCheck;
    private UpdateResult updateResult = new UpdateResult("Not checked yet");
    private DisplayMode collisionMode = DisplayMode.Disabled;
    private bool collisionShaded = true;
    private Vector3 cameraPosition = new Vector3(30, 20, 30);
    private float cameraYaw = -135;
    private float cameraPitch = -20;
    private float cameraSpeed = 30;
    private bool lookingAround;
    private SceneObject? selectedObject;
    private readonly HashSet<SceneObject> hiddenObjects = new HashSet<SceneObject>();
    private SceneArea? selectedArea;
    private bool showAreaDetails;
    private Task<string?>? folderPicking;
    public int ExitCode { get; private set; }

    public Viewer(string[] args) : base(new GameWindowSettings { UpdateFrequency = 120 }, CreateWindowSettings())
    {
        arguments = args;
    }

    private static NativeWindowSettings CreateWindowSettings()
    {
        return new NativeWindowSettings
        {
            ClientSize = new OpenTK.Mathematics.Vector2i(1440, 900),
            Title = "Banandium",
            API = ContextAPI.OpenGL,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core
        };
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        VSync = VSyncMode.On;
        ui = new ImGuiController(this);
        renderer = new SceneRenderer();
        modelRenderer = new ModelRenderer();
        RestoreSettings();
        CheckForUpdates();
        if (arguments.Length > 0 && !arguments[0].StartsWith("--"))
        {
            if (arguments.Length > 1 && !arguments[1].StartsWith("--"))
            {
                startupScene = arguments[1];
            }

            OpenDump(arguments[0]);
        }
        else if (Directory.Exists(settings.RomfsPath))
        {
            OpenDump(settings.RomfsPath);
        }

        Console.WriteLine($"OpenGL {GL.GetString(StringName.Version)} / {GL.GetString(StringName.Renderer)}");
    }

    private void OpenDump(string path)
    {
        errorMessage = "";
        pendingDumpPath = path;
        indexing = Task.Run(ReadDump);
    }

    private Dump ReadDump()
    {
        return new Dump(pendingDumpPath);
    }

    private void LoadScene(string name)
    {
        if (dump == null || loading != null || modelLoading != null)
        {
            return;
        }

        errorMessage = "";
        pendingSceneName = name;
        loading = Task.Run(ReadScene);
    }

    private SceneData ReadScene()
    {
        return dump!.Load(pendingSceneName);
    }

    private void LoadModels()
    {
        if (scene != null && models == null && modelLoading == null)
            modelLoading = Task.Run(ReadModels);
    }

    private SceneModels ReadModels()
    {
        return dump!.LoadModels(scene!.Name);
    }

    private void CheckLoadingTasks()
    {
        if (folderPicking != null && folderPicking.IsCompleted)
        {
            try
            {
                string? folder = folderPicking.GetAwaiter().GetResult();
                if (folder != null) OpenDump(folder);
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
            folderPicking = null;
        }
        if (indexing != null && indexing.IsCompleted)
        {
            try
            {
                dump = indexing.GetAwaiter().GetResult();
                settings.RomfsPath = Path.GetFullPath(pendingDumpPath);
                RememberSceneSettings();
                scene = null;
                models = null;
                modelRenderer.Upload(null);
                selectedArea = null;
                selectedObject = null;
                hiddenObjects.Clear();
                visibilityOverrides.Clear();
                highlightedObject = null;
                RebuildHierarchy();
                showAreaDetails = false;
                selectedScene = "";
                sceneFilter = "";
                renderer.Upload(new SceneData("", new MeshData(), new MeshData(), 0));
                Title = "Banandium";
                if (startupScene != "")
                {
                    LoadScene(startupScene);
                    startupScene = "";
                }
                else
                {
                    openSceneDialog = true;
                }
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }

            indexing = null;
        }

        if (loading != null && loading.IsCompleted)
        {
            try
            {
                SceneData loadedScene = loading.GetAwaiter().GetResult();
                RememberSceneSettings();
                loadedScene.Conditions.UnconditionalOnly = settings.UnconditionalOnly;
                loadedScene.Conditions.Apply();
                RestoreLayerSettings(loadedScene.ActorCollision);
                foreach (SceneLayer layer in loadedScene.AreaLayers) RestoreLayerSettings(layer);
                renderer.Upload(loadedScene);
                scene = loadedScene;
                models = null;
                modelRenderer.Upload(null);
                if (modelsVisible) LoadModels();
                selectedArea = null;
                selectedObject = null;
                hiddenObjects.Clear();
                visibilityOverrides.Clear();
                highlightedObject = null;
                RebuildHierarchy();
                showAreaDetails = false;
                PlaceCameraAtScene();
                Title = "Banandium - " + scene.Name;
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }

            loading = null;
        }

        if (modelLoading != null && modelLoading.IsCompleted)
        {
            try
            {
                models = modelLoading.GetAwaiter().GetResult();
                AttachModelConditions();
                modelRenderer.Upload(models);
                if (scene != null && visibilityOverrides.Count > 0) renderer.Upload(scene);
                RebuildHierarchy();
            }
            catch (Exception exception)
            {
                modelsVisible = false;
                ShowError(exception);
            }
            modelLoading = null;
        }
    }

    private void ShowError(Exception exception)
    {
        errorMessage = exception.Message;
        openErrorDialog = true;
        Console.Error.WriteLine(exception);
    }

    private void PlaceCameraAtScene()
    {
        if (scene == null)
        {
            return;
        }

        List<Vector3> vertices = new List<Vector3>();
        if (scene.Collision.Vertices.Count > 0)
        {
            vertices.AddRange(scene.Collision.Vertices);
        }
        else
        {
            vertices.AddRange(scene.Death.Vertices);
            vertices.AddRange(scene.IgnoreAreas.Vertices);
        }

        if (vertices.Count == 0)
        {
            return;
        }

        Vector3 minimum = vertices[0];
        Vector3 maximum = vertices[0];
        foreach (Vector3 vertex in vertices)
        {
            minimum = Vector3.Min(minimum, vertex);
            maximum = Vector3.Max(maximum, vertex);
        }

        Vector3 center = (minimum + maximum) / 2;
        float radius = Math.Max(5, (maximum - minimum).Length() * 0.5f);
        cameraYaw = -135;
        cameraPitch = -25;
        cameraPosition = center - GetCameraDirection() * radius * 2;
    }

    private Vector3 GetCameraDirection()
    {
        float yawRadians = cameraYaw * MathF.PI / 180;
        float pitchRadians = cameraPitch * MathF.PI / 180;
        Vector3 direction = new Vector3(MathF.Cos(yawRadians) * MathF.Cos(pitchRadians), MathF.Sin(pitchRadians), MathF.Sin(yawRadians) * MathF.Cos(pitchRadians));
        return Vector3.Normalize(direction);
    }

    private void ReleaseMouse()
    {
        lookingAround = false;
        CursorState = CursorState.Normal;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (sceneDialogVisible || aboutVisible || updatesVisible || ImGui.GetIO().WantCaptureMouse)
        {
            return;
        }

        if (IsFocused && scene != null)
        {
            cameraSpeed = Math.Clamp(cameraSpeed * MathF.Pow(1.15f, e.OffsetY), 0.1f, 10000);
        }
    }

    protected override void OnUpdateFrame(FrameEventArgs e)
    {
        base.OnUpdateFrame(e);
        CheckLoadingTasks();
        if (updateCheck != null && updateCheck.IsCompleted)
        {
            updateResult = updateCheck.GetAwaiter().GetResult();
            updateCheck = null;
            if (updateResult.Available) updatesVisible = true;
        }
        if (!IsFocused || sceneDialogVisible || aboutVisible || updatesVisible || KeyboardState.IsKeyPressed(Keys.Escape))
        {
            ReleaseMouse();
        }

        if (!IsFocused || sceneDialogVisible || aboutVisible || updatesVisible || ImGui.GetIO().WantCaptureKeyboard)
        {
            return;
        }

        if (scene != null && !lookingAround && MouseState.IsButtonPressed(MouseButton.Right) && IsOverScene(ImGui.GetMousePos()) && !ImGui.GetIO().WantCaptureMouse)
        {
            lookingAround = true;
            CursorState = CursorState.Grabbed;
        }

        if (!MouseState.IsButtonDown(MouseButton.Right))
        {
            ReleaseMouse();
        }

        if (lookingAround)
        {
            cameraYaw += MouseState.Delta.X * 0.12f;
            cameraPitch = Math.Clamp(cameraPitch - MouseState.Delta.Y * 0.12f, -89, 89);
        }

        if (scene == null)
        {
            return;
        }

        Vector3 forward = GetCameraDirection();
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 movement = Vector3.Zero;
        float speedMultiplier = 1;
        if (KeyboardState.IsKeyDown(Keys.W))
        {
            movement += forward;
        }

        if (KeyboardState.IsKeyDown(Keys.S))
        {
            movement -= forward;
        }

        if (KeyboardState.IsKeyDown(Keys.D))
        {
            movement += right;
        }

        if (KeyboardState.IsKeyDown(Keys.A))
        {
            movement -= right;
        }

        if (KeyboardState.IsKeyDown(Keys.Space))
        {
            movement += Vector3.UnitY;
        }

        if (KeyboardState.IsKeyDown(Keys.LeftShift))
        {
            movement -= Vector3.UnitY;
        }

        if (KeyboardState.IsKeyDown(Keys.LeftControl))
        {
            speedMultiplier = 4;
        }

        if (movement.LengthSquared() > 0)
        {
            float elapsed = (float)Math.Min(e.Time, 0.1);
            cameraPosition += Vector3.Normalize(movement) * cameraSpeed * speedMultiplier * elapsed;
        }
    }

    private void DrawDisplayModeMenu(string label, ref DisplayMode mode, ref bool shaded)
    {
        if (!ImGui.BeginMenu(label))
        {
            return;
        }

        if (ImGui.MenuItem("Translucent", "", mode == DisplayMode.SemiTransparent))
        {
            mode = DisplayMode.SemiTransparent;
        }

        if (ImGui.MenuItem("Opaque", "", mode == DisplayMode.Opaque))
        {
            mode = DisplayMode.Opaque;
        }

        if (ImGui.MenuItem("Disabled", "", mode == DisplayMode.Disabled))
        {
            mode = DisplayMode.Disabled;
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Shaded", "", shaded))
        {
            shaded = !shaded;
        }

        ImGui.EndMenu();
    }

    private void PickObject()
    {
        if (scene == null || lookingAround || sceneDialogVisible ||
            ImGui.GetIO().WantCaptureMouse || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        Vector2 mouse = ImGui.GetMousePos();
        if (!IsOverScene(mouse)) return;
        Vector3 forward = GetCameraDirection();
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        float x = (2 * (mouse.X - sceneViewPosition.X) / sceneViewSize.X - 1) * sceneViewSize.X / sceneViewSize.Y;
        float y = 1 - 2 * (mouse.Y - sceneViewPosition.Y) / sceneViewSize.Y;
        Vector3 direction = Vector3.Normalize(forward + (right * x + up * y) * MathF.Tan(MathF.PI / 6));
        SelectSceneObject(PickTrigger(mouse) ?? FindObject(cameraPosition, direction));
        if (selectedObject != null) ImGui.OpenPopup("Object menu");
    }

    private SceneObject? FindObject(Vector3 origin, Vector3 direction)
    {
        if (scene == null) return null;
        SceneObject? found = null;
        float nearest = float.PositiveInfinity;
        var meshes = new List<(MeshData Mesh, bool Enabled, bool ForceWithModel)>();
        meshes.Add((scene.Collision, collisionMode != DisplayMode.Disabled, false));
        meshes.Add((scene.ActorCollision.Mesh, scene.ActorCollision.Mode != DisplayMode.Disabled, false));
        foreach (SceneLayer layer in scene.AreaLayers)
        {
            meshes.Add((layer.Mesh, layer.Mode != DisplayMode.Disabled, true));
        }
        foreach (var group in scene.Triggers.Values)
        {
            foreach (SceneTrigger trigger in group)
                if (trigger.Layer != null && ShowTrigger(trigger)) meshes.Add((trigger.Layer.Mesh, true, true));
        }
        foreach (var entry in meshes)
        {
            foreach (MeshPart part in entry.Mesh.Parts)
            {
                bool forced = part.Object.ForceVisible && (entry.ForceWithModel || !part.Object.HasModel);
                if (!entry.Enabled && !forced) continue;
                float distance = part.HitDistance(origin, direction);
                if (distance < nearest)
                {
                    nearest = distance;
                    found = part.Object;
                }
            }
        }
        if ((modelsVisible || visibilityOverrides.Count > 0) && models != null)
        {
            foreach (ModelInstance instance in models.Instances)
            {
                if (!modelsVisible && !instance.Object.ForceVisible) continue;
                float distance = instance.HitDistance(origin, direction);
                if (distance < nearest)
                {
                    nearest = distance;
                    found = instance.Object;
                }
            }
        }
        foreach (SceneObject item in scene.Conditions.Objects.Values)
        {
            if (item.Hidden || item.Filtered || !ShowConditionOutline(item) || (!item.Inactive && item.HasBounds && item.Area?.IsIgnore != true)) continue;
            Vector3 low = item.HasBounds ? item.Minimum : item.Position - new Vector3(2);
            Vector3 high = item.HasBounds ? item.Maximum : item.Position + new Vector3(2);
            float distance = HitBounds(low, high, origin, direction);
            if (distance < nearest) { nearest = distance; found = item; }
        }
        return found;
    }

    private void DrawObjectMenu()
    {
        if (!ImGui.BeginPopup("Object menu")) return;
        if (selectedObject != null)
        {
            ImGui.TextUnformatted(selectedObject.Name);
            if (selectedObject.Trigger != null)
            {
                ImGui.TextDisabled("Trigger: " + selectedObject.Trigger.Category);
                if (selectedObject.Trigger.Surface == null) ImGui.TextDisabled("Placement marker; activation bounds unknown");
            }
            ImGui.Separator();
            DrawSelectedSwitch();
            if (ImGui.MenuItem("Hide")) HideObject(selectedObject);
            if (selectedObject.Trigger != null && ImGui.MenuItem("Hide all of type"))
            {
                triggerTypes[selectedObject.Name] = false;
                if (highlightedObject?.Name == selectedObject.Name) highlightedObject = null;
            }
            if (ImGui.MenuItem("Inspect")) InspectObject(selectedObject);
            if (selectedObject.Area != null && ImGui.MenuItem("Area details"))
            {
                selectedArea = selectedObject.Area;
                showAreaDetails = true;
            }
        }
        ImGui.EndPopup();
    }

    private void HideObject(SceneObject item)
    {
        item.Hidden = true;
        if (highlightedObject == item) highlightedObject = null;
        hiddenObjects.Add(item);
        if (item.Area == selectedArea) showAreaDetails = false;
        if (scene != null) renderer.Upload(scene);
    }

    private void UnhideAll()
    {
        foreach (SceneObject item in hiddenObjects) item.Hidden = false;
        hiddenObjects.Clear();
        if (scene != null) renderer.Upload(scene);
    }

    private void DrawAreaDetails()
    {
        if (!showAreaDetails || selectedArea == null) return;
        ImGui.SetNextWindowSize(new Vector2(440, 420), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Area details", ref showAreaDetails))
        {
            ImGui.TextUnformatted(selectedArea.Name);
            ImGui.Separator();
            string[] importantFields = { "ParameterClassName", "ModelName", "Priority", "IsIgnoreArea",
                "Translate", "Rotate", "Scale", "StageName", "IntervalIndex" };
            foreach (string name in importantFields)
            {
                if (selectedArea.Fields.TryGetValue(name, out object? value))
                {
                    ImGui.TextWrapped(name + ": " + FormatAreaField(value));
                }
            }
            if (selectedArea.Fields.GetValueOrDefault("Links") is Dictionary<string, object?> links)
            {
                ImGui.Separator();
                ImGui.TextUnformatted("Links");
                if (links.Count == 0) ImGui.TextDisabled("None");
                foreach (var link in links)
                {
                    if (!ImGui.TreeNode(link.Key)) continue;
                    if (link.Value is List<object?> targets)
                    {
                        foreach (object? target in targets)
                        {
                            if (target is not Dictionary<string, object?> fields) continue;
                            ImGui.TextWrapped("ObjectName: " + fields.GetValueOrDefault("ObjectName"));
                            ImGui.TextWrapped("Id: " + fields.GetValueOrDefault("Id"));
                            ImGui.Separator();
                        }
                    }
                    ImGui.TreePop();
                }
            }
            if (ImGui.CollapsingHeader("All placement fields"))
            {
                foreach (var field in selectedArea.Fields)
                {
                    if (field.Key == "Links") continue;
                    ImGui.TextWrapped(field.Key + ": " + FormatAreaField(field.Value));
                }
            }
            ImGui.TextWrapped("Authored placement. Use the inspector for switch preview and links.");
            if (ImGui.Button("Inspect")) InspectObject(selectedArea.Object);
        }
        ImGui.End();
    }

    private static string FormatAreaField(object? value)
    {
        if (value == null) return "null";
        if (value is string text) return text;
        if (value is Dictionary<string, object?> vector && vector.ContainsKey("X") &&
            vector.ContainsKey("Y") && vector.ContainsKey("Z"))
        {
            return $"X: {Convert.ToSingle(vector["X"]):0.###}, Y: {Convert.ToSingle(vector["Y"]):0.###}, Z: {Convert.ToSingle(vector["Z"]):0.###}";
        }
        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    private void ChooseDumpFolder()
    {
        ReleaseMouse();
        if (!OperatingSystem.IsWindows())
        {
            folderPicking = Task.Run(DumpFolderDialog.Show);
            return;
        }
        try
        {
            string? folder = DumpFolderDialog.Show();
            if (folder != null) OpenDump(folder);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
        {
            return;
        }

        bool busy = indexing != null || loading != null || modelLoading != null || folderPicking != null;
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("Open dump...", "", false, !busy))
            {
                ChooseDumpFolder();
            }

            if (ImGui.MenuItem("Exit"))
            {
                Close();
            }

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Change scene...", "", false, dump != null && !busy))
            {
                openSceneDialog = true;
            }

            if (ImGui.MenuItem("Unhide all", "", false, hiddenObjects.Count > 0)) UnhideAll();
            if (ImGui.MenuItem("Hierarchy and inspector", "", sidebarVisible)) sidebarVisible = !sidebarVisible;
            DrawConditionMenu();
            DrawTriggerMenu();
            if (ImGui.MenuItem("Conditional outlines", "", conditionalOutlinesVisible))
                conditionalOutlinesVisible = !conditionalOutlinesVisible;
            ImGui.Separator();
            if (ImGui.MenuItem("Textured models", "", modelsVisible, scene != null && !busy))
            {
                modelsVisible = !modelsVisible;
                if (modelsVisible) LoadModels();
            }
            DrawDisplayModeMenu("Collision", ref collisionMode, ref collisionShaded);
            if (scene != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(scene.ActorCollision.Color, 1));
                DrawDisplayModeMenu("Actor collision", ref scene.ActorCollision.Mode, ref scene.ActorCollision.Shaded);
                ImGui.PopStyleColor();
                ImGui.Separator();
                foreach (SceneLayer layer in scene.AreaLayers)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(layer.Color, 1));
                    DrawDisplayModeMenu(layer.Name, ref layer.Mode, ref layer.Shaded);
                    ImGui.PopStyleColor();
                }
            }
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("Help"))
        {
            if (ImGui.MenuItem("About"))
            {
                aboutVisible = true;
                ReleaseMouse();
            }
            if (ImGui.MenuItem("Check for updates", "", false, updateCheck == null))
            {
                updatesVisible = true;
                ReleaseMouse();
                CheckForUpdates();
            }
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }

    private void CheckForUpdates()
    {
        if (updateCheck != null) return;
        updateResult = new UpdateResult("Checking for updates...");
        updateCheck = Updates.CheckAsync();
    }

    private void DrawAbout()
    {
        if (!aboutVisible) return;
        ImGui.SetNextWindowSize(new Vector2(410, 0), ImGuiCond.Appearing);
        if (ImGui.Begin("About Banandium", ref aboutVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Version " + Updates.CurrentTag);
            ImGui.TextUnformatted("Banandium was developed by JakeZSR");
        }
        ImGui.End();
    }

    private void DrawUpdates()
    {
        if (!updatesVisible) return;
        ImGui.SetNextWindowSize(new Vector2(410, 0), ImGuiCond.Appearing);
        if (ImGui.Begin("Updates", ref updatesVisible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(updateResult.Message);
            if (updateResult.Available)
            {
                if (ImGui.Button("View release"))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Updates.ReleasePage)
                        {
                            UseShellExecute = true
                        });
                    }
                    catch (System.ComponentModel.Win32Exception exception)
                    {
                        ShowError(exception);
                    }
                }
            }
        }
        ImGui.End();
    }

    private void DrawSceneDialog()
    {
        if (openSceneDialog)
        {
            ReleaseMouse();
            sceneDialogVisible = true;
            if (scene != null)
            {
                selectedScene = scene.Name;
            }

            ImGui.OpenPopup("Change scene");
            openSceneDialog = false;
        }

        ImGui.SetNextWindowSize(new Vector2(590, 540), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(new Vector2(ClientSize.X / 2f, ClientSize.Y / 2f), ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.BeginPopupModal("Change scene", ref sceneDialogVisible, ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        if (dump != null)
        {
            ImGui.Checkbox("Display Common Names", ref displayCommonNames);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", "Filter scenes...", ref sceneFilter, 160);
            ImGui.BeginChild("scene-list", new Vector2(0, 330), ImGuiChildFlags.Borders);
            foreach (string name in dump.Scenes.Keys)
            {
                string commonName = SceneNames.GetCommonName(name);
                if (!name.Contains(sceneFilter, StringComparison.OrdinalIgnoreCase) &&
                    !commonName.Contains(sceneFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ImGui.PushID(name);
                if (ImGui.Selectable(displayCommonNames ? commonName : name, selectedScene == name))
                {
                    selectedScene = name;
                }
                ImGui.PopID();
            }

            ImGui.EndChild();
        }

        ImGui.BeginDisabled(selectedScene == "");
        if (ImGui.Button("Open scene"))
        {
            LoadScene(selectedScene);
            sceneDialogVisible = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            sceneDialogVisible = false;
            ImGui.CloseCurrentPopup();
        }

        if (scene != null && ImGui.CollapsingHeader("Current scene details"))
        {
            ImGui.Text($"{scene.Collision.Triangles:N0} collision triangles; {scene.DeathAreas} death areas");
            ImGui.Text($"{scene.CollisionActors:N0} actors with collision; {scene.ActorCollision.Mesh.Triangles:N0} actor collision triangles");
            ImGui.Text($"{scene.UnsupportedActorShapes:N0} unsupported actor shape definitions or bindings");
            ImGui.TextUnformatted("Actor collision uses placed poses; switches and animation are not simulated");
            if (modelLoading != null) ImGui.TextUnformatted("Loading textured models...");
            if (models != null)
            {
                ImGui.Text($"{models.Instances.Count:N0} model parts");
                ImGui.Text($"{models.MissingModels} missing models; {models.SkippedShapes} unsupported shapes; {models.MissingTextures} missing or unsupported textures");
            }
        }

        ImGui.EndPopup();
    }

    private void DrawErrorDialog()
    {
        if (openErrorDialog)
        {
            ReleaseMouse();
            ImGui.OpenPopup("Unable to load");
            openErrorDialog = false;
        }

        ImGui.SetNextWindowSize(new Vector2(500, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Unable to load", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped(errorMessage);
        if (ImGui.Button("OK"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    protected override void OnRenderFrame(FrameEventArgs e)
    {
        base.OnRenderFrame(e);
        if (ClientSize.X <= 0 || ClientSize.Y <= 0)
        {
            return;
        }

        ui.Begin(this, (float)e.Time, lookingAround);
        DrawMenuBar();
        UpdateSceneView();
        DrawSidebar();
        DrawSceneDialog();
        DrawAbout();
        DrawUpdates();
        DrawErrorDialog();
        PickObject();
        DrawObjectMenu();
        DrawAreaDetails();
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.ClearColor(0, 0, 0, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        Vector3 forward = GetCameraDirection();
        Matrix4x4 view = Matrix4x4.CreateLookAt(cameraPosition, cameraPosition + forward, Vector3.UnitY);
        Matrix4x4 projection = CreateProjection();
        float scaleX = (float)FramebufferSize.X / ClientSize.X;
        float scaleY = (float)FramebufferSize.Y / ClientSize.Y;
        int left = (int)(sceneViewPosition.X * scaleX);
        int top = (int)(sceneViewPosition.Y * scaleY);
        GL.Viewport(left, 0, Math.Max(1, FramebufferSize.X - left), Math.Max(1, FramebufferSize.Y - top));
        if (modelsVisible || visibilityOverrides.Count > 0) modelRenderer.Draw(view * projection, modelsVisible);
        UpdateTriggerLayers();
        renderer.Draw(view * projection, cameraPosition, forward, collisionMode, collisionShaded);
        var overlay = ImGui.GetBackgroundDrawList();
        overlay.PushClipRect(sceneViewPosition, sceneViewPosition + sceneViewSize, true);
        DrawConditionOutlines(view * projection);
        DrawTriggers(view * projection);
        overlay.PopClipRect();
        ui.End();
        SwapBuffers();
    }

    private Matrix4x4 CreateProjection()
    {
        float near = 0.05f;
        float far = 100000;
        float focalLength = 1 / MathF.Tan(MathF.PI / 6);
        float aspect = sceneViewSize.X / sceneViewSize.Y;
        return new Matrix4x4(focalLength / aspect, 0, 0, 0, 0, focalLength, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0);
    }

    protected override void OnUnload()
    {
        SaveSettings();
        ReleaseMouse();
        renderer.Dispose();
        modelRenderer.Dispose();
        ui.Dispose();
        base.OnUnload();
    }
}
