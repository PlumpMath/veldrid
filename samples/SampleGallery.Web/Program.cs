﻿using Humanizer;
using Samples.Helpers;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Veldrid;
using WebAssembly;

namespace Samples
{
    class Program
    {
        const int CanvasWidth = 640;
        const int CanvasHeight = 480;
        static readonly Vector4 CanvasColor = new Vector4(255, 0, 255, 255);

        static Action<double> loop = new Action<double>(Loop);
        static double previousMilliseconds;

        static JSObject window;
        private static GraphicsDevice _gd;
        private static Stopwatch _sw;
        private static CommandBuffer[] _cbs;
        private static Fence _frameFence;
        private static Pipeline _pipeline;
        private static ResourceLayout _layout;
        private static int _frameCount;
        private static DeviceBuffer _mvpBuffer;
        private static ResourceSet _set;
        private static DeviceBuffer _vb;
        private static DeviceBuffer _ib;

        static void Main()
        {
            // Let's first check if we can continue with WebGL2 instead of crashing.
            if (!isBrowserSupportsWebGL2())
            {
                HtmlHelper.AddParagraph("We are sorry, but your browser does not seem to support WebGL2.");
                HtmlHelper.AddParagraph("See the <a href=\"https://github.com/WaveEngine/WebGL.NET\">GitHub repo</a>.");
                return;
            }

            HtmlHelper.AddHeader1("Veldrid WebGL Backend");

            var divCanvasName = $"div_canvas";
            var canvasName = $"canvas";
            var canvas = HtmlHelper.AddCanvas(divCanvasName, canvasName, CanvasWidth, CanvasHeight);
            GraphicsDeviceOptions options = new GraphicsDeviceOptions();
            _gd = GraphicsDevice.CreateWebGL(options, canvas);
            _sw = Stopwatch.StartNew();
            uint bufferCount = _gd.MainSwapchain.BufferCount;

            _frameFence = _gd.ResourceFactory.CreateFence(false);
            (_pipeline, _layout) = CreateQuadPipeline(
                _gd.ResourceFactory,
                _gd.MainSwapchain.Framebuffers[0].OutputDescription);
            _mvpBuffer = _gd.ResourceFactory.CreateBuffer(64 * 3 * bufferCount, BufferUsage.UniformBuffer);
            _set = _gd.ResourceFactory.CreateResourceSet(new ResourceSetDescription(_layout, _mvpBuffer));

            Vector3[] vertices = GetCubeVertices();
            _vb = _gd.ResourceFactory.CreateBuffer(
                (uint)(Unsafe.SizeOf<Vector3>() * vertices.Length),
                BufferUsage.VertexBuffer);
            _vb.Update(0, vertices);

            ushort[] indices = GetCubeIndices();
            _ib = _gd.ResourceFactory.CreateBuffer((uint)(indices.Length * 2), BufferUsage.IndexBuffer);
            _ib.Update(0, indices);

            _cbs = new CommandBuffer[bufferCount];
            for (uint i = 0; i < bufferCount; i++)
            {
                _cbs[i] = _gd.ResourceFactory.CreateCommandBuffer(CommandBufferFlags.Reusable);
                RecordCommands(_cbs[i], i);
            }

            RequestAnimationFrame(0);
        }

        private static void SetNewCanvasSize(JSObject canvasObject, int newWidth, int newHeight)
        {
            canvasObject.SetObjectProperty("width", newWidth);
            canvasObject.SetObjectProperty("height", newHeight);
        }

        static void Loop(double milliseconds)
        {
            var elapsedMilliseconds = milliseconds - previousMilliseconds;
            previousMilliseconds = milliseconds;

            RequestAnimationFrame(0);
        }

        static void RequestAnimationFrame(uint frameIndex)
        {
            if (window == null)
            {
                window = (JSObject)Runtime.GetGlobalObject();
            }

            _mvpBuffer.Update(64 * frameIndex,
                new Matrix4x4[]
                {
                    Matrix4x4.CreateRotationX((float)Math.Cos(_sw.Elapsed.TotalSeconds) * .3f)
                        * Matrix4x4.CreateRotationY((float)_sw.Elapsed.TotalSeconds)
                        * Matrix4x4.CreateWorld(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY),
                    Matrix4x4.CreateLookAt(Vector3.UnitZ * 3 + Vector3.UnitY * 1, Vector3.Zero, Vector3.UnitY),
                    Matrix4x4.CreatePerspectiveFieldOfView(1f, CanvasWidth / (float)CanvasHeight, 0.1f, 100f),
                });

            _gd.SubmitCommands(_cbs[0], null, null, _frameFence);
            _frameFence.Wait();
            _frameFence.Reset();
            _frameCount += 1;

            window.Invoke("requestAnimationFrame", loop);
        }

        static void RecordCommands(CommandBuffer cb, uint frameIndex)
        {
            cb.BeginRenderPass(
                _gd.MainSwapchain.Framebuffers[frameIndex],
                LoadAction.Clear,
                StoreAction.Store,
                new RgbaFloat(
                    (float)Math.Cos(_sw.Elapsed.TotalSeconds),
                    (float)Math.Sin(_sw.Elapsed.TotalSeconds),
                    0,
                    1),
                1f);
            cb.BindPipeline(_pipeline);
            cb.BindVertexBuffer(0, _vb);
            cb.BindIndexBuffer(_ib, IndexFormat.UInt16);
            Span<uint> offsets = stackalloc uint[1];
            offsets[0] = 64 * frameIndex;
            cb.BindGraphicsResourceSet(0, _set, offsets);
            cb.DrawIndexed(36);
            cb.EndRenderPass();
        }

        static bool isBrowserSupportsWebGL2()
        {
            if (window == null)
            {
                window = (JSObject)Runtime.GetGlobalObject();
            }

            // This is a very simple check for WebGL2 support.
            return window.GetObjectProperty("WebGL2RenderingContext") != null;
        }

        private static (Pipeline, ResourceLayout) CreateQuadPipeline(ResourceFactory factory, OutputDescription outputs)
        {
            const string VS =
@"#version 300 es

in vec3 Position;
out vec4 fsin_color;

uniform MvpBuffer
{
    mat4 Model;
    mat4 View;
    mat4 Projection;
};

void main()
{
    vec4 Colors[3];
    Colors[0] = vec4(1, 0, 0, 1);
    Colors[1] = vec4(0, 1, 0, 1);
    Colors[2] = vec4(0, 0, 1, 1);

    gl_Position = Projection * View * Model * vec4(Position, 1);
    fsin_color = Colors[gl_VertexID % 3];
}
";

            const string FS =
@"#version 300 es
precision highp float;

in vec4 fsin_color;
out vec4 fsout_color;
void main()
{
    fsout_color = fsin_color;
}
";

            Shader vs = factory.CreateShader(
                new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(VS), "main"));
            Shader fs = factory.CreateShader(
                new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FS), "main"));

            var layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                new ResourceLayoutElementDescription(
                    "MvpBuffer",
                    ResourceKind.UniformBuffer,
                    ShaderStages.Vertex,
                    ResourceLayoutElementOptions.DynamicBinding)));

            Pipeline pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
                BlendStateDescription.SingleOverrideBlend,
                DepthStencilStateDescription.DepthOnlyLessEqual,
                RasterizerStateDescription.Default,
                PrimitiveTopology.TriangleList,
                new ShaderSetDescription(
                    new[]
                    {
                        new VertexLayoutDescription(
                            new VertexElementDescription(
                                "Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3))
                    },
                    new[] { vs, fs }),
                layout,
                outputs));
            return (pipeline, layout);
        }

        private static Vector3[] GetCubeVertices()
        {
            Vector3[] vertices = new Vector3[]
            {
                // Top
                new Vector3(-0.5f, +0.5f, -0.5f),
                new Vector3(+0.5f, +0.5f, -0.5f),
                new Vector3(+0.5f, +0.5f, +0.5f),
                new Vector3(-0.5f, +0.5f, +0.5f),

                new Vector3(-0.5f,-0.5f, +0.5f),
                new Vector3(+0.5f,-0.5f, +0.5f),
                new Vector3(+0.5f,-0.5f, -0.5f),
                new Vector3(-0.5f,-0.5f, -0.5f),

                new Vector3(-0.5f, +0.5f, -0.5f),
                new Vector3(-0.5f, +0.5f, +0.5f),
                new Vector3(-0.5f, -0.5f, +0.5f),
                new Vector3(-0.5f, -0.5f, -0.5f),

                new Vector3(+0.5f, +0.5f, +0.5f),
                new Vector3(+0.5f, +0.5f, -0.5f),
                new Vector3(+0.5f, -0.5f, -0.5f),
                new Vector3(+0.5f, -0.5f, +0.5f),

                new Vector3(+0.5f, +0.5f, -0.5f),
                new Vector3(-0.5f, +0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(+0.5f, -0.5f, -0.5f),

                new Vector3(-0.5f, +0.5f, +0.5f),
                new Vector3(+0.5f, +0.5f, +0.5f),
                new Vector3(+0.5f, -0.5f, +0.5f),
                new Vector3(-0.5f, -0.5f, +0.5f),
            };

            return vertices;
        }

        private static ushort[] GetCubeIndices()
        {
            ushort[] indices =
            {
                0,1,2, 0,2,3,
                4,5,6, 4,6,7,
                8,9,10, 8,10,11,
                12,13,14, 12,14,15,
                16,17,18, 16,18,19,
                20,21,22, 20,22,23,
            };

            return indices;
        }
    }
}
