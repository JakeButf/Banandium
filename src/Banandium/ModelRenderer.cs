using System.Numerics;
using BfresLibrary.GX2;
using OpenTK.Graphics.OpenGL4;

namespace Banandium;

public sealed class ModelRenderer : IDisposable
{
    private sealed record MeshBuffers(int Vao, int Vbo, int Ebo, Vector3 Minimum, Vector3 Maximum,
        int Color, int Opacity, int Sampler);

    private sealed class DrawItem
    {
        public ModelInstance Instance;
        public MeshBuffers Buffers;
        public Vector3 Minimum = new Vector3(float.PositiveInfinity);
        public Vector3 Maximum = new Vector3(float.NegativeInfinity);

        public DrawItem(ModelInstance instance, MeshBuffers buffers)
        {
            Instance = instance;
            Buffers = buffers;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? buffers.Minimum.X : buffers.Maximum.X,
                    (i & 2) == 0 ? buffers.Minimum.Y : buffers.Maximum.Y,
                    (i & 4) == 0 ? buffers.Minimum.Z : buffers.Maximum.Z);
                Vector3 point = Vector3.Transform(corner, instance.Transform);
                Minimum = Vector3.Min(Minimum, point);
                Maximum = Vector3.Max(Maximum, point);
            }
        }
    }

    private readonly int program;
    private readonly int matrixLocation, modelLocation, eyeLocation, lightLocation, tintLocation;
    private readonly List<DrawItem> items = new List<DrawItem>();
    private readonly ViewFrustum frustum = new ViewFrustum();
    private readonly Dictionary<(TextureWrapMode X, TextureWrapMode Y), int> samplers = new Dictionary<(TextureWrapMode, TextureWrapMode), int>();
    private readonly Dictionary<ModelMesh, MeshBuffers> meshes = new Dictionary<ModelMesh, MeshBuffers>();
    private readonly Dictionary<ModelTexture, int> textures = new Dictionary<ModelTexture, int>();
    private readonly int white;
    private SceneModels? scene;

    public ModelRenderer()
    {
        program = Shaders.Create("""
        #version 330 core
        layout(location=0) in vec3 position;
        layout(location=1) in vec3 normal;
        layout(location=2) in vec2 uv;
        layout(location=3) in vec4 vertexColor;
        uniform mat4 matrix;
        uniform mat4 model;
        out vec2 texCoord;
        out vec3 surfaceNormal;
        out vec3 worldPosition;
        out vec4 surfaceTint;
        void main() {
            vec4 worldPos = model * vec4(position, 1.0);
            gl_Position = matrix * worldPos;
            surfaceNormal = transpose(inverse(mat3(model))) * normal;
            worldPosition = worldPos.xyz;
            texCoord = uv;
            surfaceTint = vertexColor;
        }
        """, """
        #version 330 core
        in vec2 texCoord;
        in vec3 surfaceNormal;
        in vec3 worldPosition;
        in vec4 surfaceTint;
        uniform sampler2D albedo;
        uniform sampler2D opacity;
        uniform vec4 tint;
        uniform vec3 eye;
        uniform vec3 lightDirection;
        out vec4 color;
        void main() {
            vec4 sampleColor = texture(albedo, texCoord);
            if (sampleColor.a * tint.a * surfaceTint.a * texture(opacity, texCoord).r < 0.5) discard;

            vec3 normal = normalize(surfaceNormal);
            if (!gl_FrontFacing) normal = -normal;

            vec3 lightDir = normalize(lightDirection);

            float keyLight = max(dot(normal, lightDir), 0.0);
            float fillLight = max(dot(normal, -lightDir), 0.0);

            float light = 0.25 + 0.75 * keyLight + 0.60 * fillLight;

            float depth = length(eye - worldPosition);
            float depthFactor = clamp((depth - 5.0) / 250.0, 0.0, 1.0);
            depthFactor = pow(depthFactor, 0.4);

            float proximityLight = (1.0 - depthFactor);
            light += proximityLight;

            vec3 baseColor = sampleColor.rgb * tint.rgb * surfaceTint.rgb;
            vec3 finalColor = baseColor * light;

            color = vec4(pow(max(finalColor, vec3(0.0)), vec3(1.0 / 2.2)), 1.0);
        }
""");
        matrixLocation = GL.GetUniformLocation(program, "matrix");
        modelLocation = GL.GetUniformLocation(program, "model");
        tintLocation = GL.GetUniformLocation(program, "tint");
        eyeLocation = GL.GetUniformLocation(program, "eye");
        lightLocation = GL.GetUniformLocation(program, "lightDirection");
        GL.UseProgram(program);
        GL.Uniform1(GL.GetUniformLocation(program, "albedo"), 0);
        GL.Uniform1(GL.GetUniformLocation(program, "opacity"), 1);
        GL.UseProgram(0);
        white = UploadTexture(new ModelTexture(1, 1, new byte[] {255, 255, 255, 255}));
    }

    public void Upload(SceneModels? models)
    {
        Clear();
        scene = models;
        if (scene == null) return;
        foreach (ModelInstance instance in scene.Instances)
        {
            ModelMesh mesh = instance.Mesh;
            if (meshes.ContainsKey(mesh)) continue;
            int vao = GL.GenVertexArray(), vbo = GL.GenBuffer(), ebo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, mesh.Vertices.Length * sizeof(float), mesh.Vertices, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, mesh.Indices.Length * sizeof(uint), mesh.Indices, BufferUsageHint.StaticDraw);
            for (int i = 0; i < 4; i++) GL.EnableVertexAttribArray(i);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 48, 0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 48, 12);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 48, 24);
            GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, 48, 32);
            foreach (ModelTexture? texture in new[] { mesh.Color, mesh.Opacity })
                if (texture != null && !textures.ContainsKey(texture)) textures.Add(texture, UploadTexture(texture));
            Vector3 minimum = new Vector3(float.PositiveInfinity);
            Vector3 maximum = new Vector3(float.NegativeInfinity);
            for (int i = 0; i < mesh.Vertices.Length; i += 12)
            {
                Vector3 point = new Vector3(mesh.Vertices[i], mesh.Vertices[i + 1], mesh.Vertices[i + 2]);
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
            }
            int color = mesh.Color == null ? white : textures[mesh.Color];
            int opacity = mesh.Opacity == null ? white : textures[mesh.Opacity];
            meshes.Add(mesh, new MeshBuffers(vao, vbo, ebo, minimum, maximum, color, opacity, GetSampler(mesh)));
        }
        foreach (ModelInstance instance in scene.Instances)
            items.Add(new DrawItem(instance, meshes[instance.Mesh]));
        GL.BindVertexArray(0);
    }

    private int GetSampler(ModelMesh mesh)
    {
        var wrap = (X: Wrap(mesh.WrapX), Y: Wrap(mesh.WrapY));
        if (samplers.TryGetValue(wrap, out int sampler)) return sampler;
        sampler = GL.GenSampler();
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapS, (int)wrap.X);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapT, (int)wrap.Y);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        samplers.Add(wrap, sampler);
        return sampler;
    }

    private static int UploadTexture(ModelTexture texture)
    {
        int id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);
        GL.TexImage2D(TextureTarget.Texture2D, 0, texture.Srgb ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8, texture.Width,
            texture.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, texture.Pixels);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        return id;
    }

    private static TextureWrapMode Wrap(GX2TexClamp mode)
    {
        if (mode == GX2TexClamp.Wrap) return TextureWrapMode.Repeat;
        if (mode == GX2TexClamp.Mirror) return TextureWrapMode.MirroredRepeat;
        return TextureWrapMode.ClampToEdge;
    }

    public void Draw(Matrix4x4 matrix, Vector3 eye, bool modelsVisible = true)
    {
        if (scene == null) return;
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(1f, 1f);
        GL.UseProgram(program);
        Shaders.Matrix(matrixLocation, matrix);
        frustum.Update(matrix);
        GL.Uniform3(eyeLocation, eye.X, eye.Y, eye.Z);
        Vector3 light = Vector3.Normalize(new Vector3(-0.4f, 1.0f, 0.3f));
        GL.Uniform3(lightLocation, light.X, light.Y, light.Z);
        int lastVao = -1, lastColor = -1, lastOpacity = -1, lastSampler = -1;
        Vector4 lastTint = new Vector4(float.NaN);
        Vector4 lastEye = new Vector4(float.NaN);
        foreach (DrawItem item in items)
        {
            ModelInstance instance = item.Instance;
            if (!modelsVisible && !instance.Object.ForceVisible) continue;
            if (!instance.Object.Visible || !frustum.Intersects(item.Minimum, item.Maximum)) continue;
            ModelMesh mesh = instance.Mesh;
            MeshBuffers buffers = item.Buffers;
            Vector4 tint = mesh.Tint;
            if (mesh.UsePlacementColor && instance.MaterialColor.HasValue) tint = instance.MaterialColor.Value;
            if (tint != lastTint)
            {
                GL.Uniform4(tintLocation, tint.X, tint.Y, tint.Z, tint.W);
                lastTint = tint;
            }
            Shaders.Matrix(modelLocation, instance.Transform);
            if (buffers.Vao != lastVao)
            {
                GL.BindVertexArray(buffers.Vao);
                lastVao = buffers.Vao;
            }
            if (buffers.Color != lastColor)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, buffers.Color);
                lastColor = buffers.Color;
            }
            if (buffers.Opacity != lastOpacity)
            {
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, buffers.Opacity);
                lastOpacity = buffers.Opacity;
            }
            if (buffers.Sampler != lastSampler)
            {
                GL.BindSampler(0, buffers.Sampler);
                GL.BindSampler(1, buffers.Sampler);
                lastSampler = buffers.Sampler;
            }
            GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, 0);
        }
        GL.BindSampler(0, 0);
        GL.BindSampler(1, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
    }

    private void Clear()
    {
        scene = null;
        foreach (MeshBuffers mesh in meshes.Values)
        {
            GL.DeleteBuffer(mesh.Vbo);
            GL.DeleteBuffer(mesh.Ebo);
            GL.DeleteVertexArray(mesh.Vao);
        }
        foreach (int texture in textures.Values) GL.DeleteTexture(texture);
        foreach (int sampler in samplers.Values) GL.DeleteSampler(sampler);
        samplers.Clear();
        items.Clear();
        meshes.Clear();
        textures.Clear();
    }

    public void Dispose()
    {
        Clear();
        GL.DeleteTexture(white);
        GL.DeleteProgram(program);
    }
}
