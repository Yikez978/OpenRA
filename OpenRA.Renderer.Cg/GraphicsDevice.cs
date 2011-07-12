#region Copyright & License Information
/*
* Copyright 2007-2011 The OpenRA Developers (see AUTHORS)
* This file is part of OpenRA, which is free software. It is made 
* available to you under the terms of the GNU General Public License
* as published by the Free Software Foundation. For more information,
* see COPYING.
*/
#endregion

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using OpenRA.FileFormats.Graphics;
using OpenRA.Renderer.SdlCommon;
using Tao.Cg;
using Tao.OpenGl;
using Tao.Sdl;

[assembly: Renderer(typeof(OpenRA.Renderer.Cg.DeviceFactory))]

namespace OpenRA.Renderer.Cg
{
	public class DeviceFactory : IDeviceFactory
	{
		public IGraphicsDevice Create(Size size, WindowMode windowMode)
		{
			return new GraphicsDevice( size, windowMode );
		}
	}

	public class GraphicsDevice : IGraphicsDevice
	{
		Size windowSize;
		internal IntPtr cgContext;
		internal int vertexProfile, fragmentProfile;

		IntPtr surf;

		public Size WindowSize { get { return windowSize; } }

		static Tao.Cg.Cg.CGerrorCallbackFuncDelegate CgErrorCallback = () =>
		{
			var err = Tao.Cg.Cg.cgGetError();
			var msg = "CG Error: {0}: {1}".F(err, Tao.Cg.Cg.cgGetErrorString(err));
			ErrorHandler.WriteGraphicsLog(msg);
			throw new InvalidOperationException("CG Error. See graphics.log for details");
		};

		public GraphicsDevice( Size size, WindowMode window )
		{
			Console.WriteLine("Using Cg renderer");
			Sdl.SDL_Init( Sdl.SDL_INIT_NOPARACHUTE | Sdl.SDL_INIT_VIDEO );
			Sdl.SDL_GL_SetAttribute( Sdl.SDL_GL_DOUBLEBUFFER, 1 );
			Sdl.SDL_GL_SetAttribute( Sdl.SDL_GL_RED_SIZE, 8 );
			Sdl.SDL_GL_SetAttribute( Sdl.SDL_GL_GREEN_SIZE, 8 );
			Sdl.SDL_GL_SetAttribute( Sdl.SDL_GL_BLUE_SIZE, 8 );
			Sdl.SDL_GL_SetAttribute( Sdl.SDL_GL_ALPHA_SIZE, 0 );

			int windowFlags = 0;
			switch( window )
			{
			case WindowMode.Fullscreen:
				windowFlags |= Sdl.SDL_FULLSCREEN;
				break;
			case WindowMode.PseudoFullscreen:
				windowFlags |= Sdl.SDL_NOFRAME;
				Environment.SetEnvironmentVariable( "SDL_VIDEO_WINDOW_POS", "0,0" );
				break;
			case WindowMode.Windowed:
				Environment.SetEnvironmentVariable( "SDL_VIDEO_CENTERED", "1" );
				break;
			default:
				break;
			}

			var info = (Sdl.SDL_VideoInfo) Marshal.PtrToStructure(
				Sdl.SDL_GetVideoInfo(), typeof(Sdl.SDL_VideoInfo));
			Console.WriteLine("Desktop resolution: {0}x{1}",
				info.current_w, info.current_h);

			if (size.Width == 0 && size.Height == 0)
			{
				Console.WriteLine("No custom resolution provided, using desktop resolution");
				size = new Size( info.current_w, info.current_h );
			}

			Console.WriteLine("Using resolution: {0}x{1}", size.Width, size.Height);

			surf = Sdl.SDL_SetVideoMode( size.Width, size.Height, 0, Sdl.SDL_OPENGL | windowFlags );
			if (surf == IntPtr.Zero)
				Console.WriteLine("Failed to set video mode.");

			Sdl.SDL_WM_SetCaption( "OpenRA", "OpenRA" );
			Sdl.SDL_ShowCursor( 0 );
			Sdl.SDL_EnableUNICODE( 1 );
			Sdl.SDL_EnableKeyRepeat( Sdl.SDL_DEFAULT_REPEAT_DELAY, Sdl.SDL_DEFAULT_REPEAT_INTERVAL );

			ErrorHandler.CheckGlError();

			// Test for required extensions
			var required = new string[]
			{
				"GL_ARB_vertex_program",
				"GL_ARB_fragment_program",
				"GL_ARB_vertex_buffer_object",
			};

			var extensions = Gl.glGetString(Gl.GL_EXTENSIONS);
			if (extensions == null)
				Console.WriteLine("Failed to fetch GL_EXTENSIONS, this is bad.");

			var missingExtensions = required.Where( r => !extensions.Contains(r) ).ToArray();

			if (missingExtensions.Any())
			{
				ErrorHandler.WriteGraphicsLog("Unsupported GPU: Missing extensions: {0}"
					.F(string.Join(",", missingExtensions)));
				throw new InvalidProgramException("Unsupported GPU. See graphics.log for details.");
			}

			windowSize = size;

			cgContext = Tao.Cg.Cg.cgCreateContext();

			Tao.Cg.Cg.cgSetErrorCallback( CgErrorCallback );

			Tao.Cg.CgGl.cgGLRegisterStates( cgContext );
			Tao.Cg.CgGl.cgGLSetManageTextureParameters( cgContext, true );
			vertexProfile = CgGl.cgGLGetLatestProfile( CgGl.CG_GL_VERTEX );
			fragmentProfile = CgGl.cgGLGetLatestProfile( CgGl.CG_GL_FRAGMENT );

			Gl.glEnableClientState( Gl.GL_VERTEX_ARRAY );
			ErrorHandler.CheckGlError();
			Gl.glEnableClientState( Gl.GL_TEXTURE_COORD_ARRAY );
			ErrorHandler.CheckGlError();

			Sdl.SDL_SetModState( 0 );	// i have had enough.
		}

		public void EnableScissor( int left, int top, int width, int height )
		{
			if( width < 0 ) width = 0;
			if( height < 0 ) height = 0;
			Gl.glScissor( left, windowSize.Height - ( top + height ), width, height );
			ErrorHandler.CheckGlError();
			Gl.glEnable( Gl.GL_SCISSOR_TEST );
			ErrorHandler.CheckGlError();
		}

		public void DisableScissor()
		{
			Gl.glDisable( Gl.GL_SCISSOR_TEST );
			ErrorHandler.CheckGlError();
		}

		public void Clear( Color c )
		{
			Gl.glClearColor( 0, 0, 0, 0 );
			ErrorHandler.CheckGlError();
			Gl.glClear( Gl.GL_COLOR_BUFFER_BIT );
			ErrorHandler.CheckGlError();
		}

		MouseButton lastButtonBits = (MouseButton)0;

		MouseButton MakeButton( byte b )
		{
			return b == Sdl.SDL_BUTTON_LEFT ? MouseButton.Left
				: b == Sdl.SDL_BUTTON_RIGHT ? MouseButton.Right
				: b == Sdl.SDL_BUTTON_MIDDLE ? MouseButton.Middle
				: b == Sdl.SDL_BUTTON_WHEELDOWN ? MouseButton.WheelDown
				: b == Sdl.SDL_BUTTON_WHEELUP ? MouseButton.WheelUp
				: 0;
		}

		Modifiers MakeModifiers( int raw )
		{
			return ( ( raw & Sdl.KMOD_ALT ) != 0 ? Modifiers.Alt : 0 )
				 | ( ( raw & Sdl.KMOD_CTRL ) != 0 ? Modifiers.Ctrl : 0 )
				 | ( ( raw & Sdl.KMOD_META ) != 0 ? Modifiers.Meta : 0 )
				 | ( ( raw & Sdl.KMOD_SHIFT ) != 0 ? Modifiers.Shift : 0 );
		}

		bool HandleSpecialKey( KeyInput k )
		{
			switch( k.VirtKey )
			{
			case Sdl.SDLK_F13:
				var path = Environment.GetFolderPath( Environment.SpecialFolder.Personal )
					+ Path.DirectorySeparatorChar + DateTime.UtcNow.ToString( "OpenRA-yyyy-MM-ddThhmmssZ" ) + ".bmp";
				Sdl.SDL_SaveBMP( surf, path );
				return true;

			case Sdl.SDLK_F4:
				if( k.Modifiers.HasModifier( Modifiers.Alt ) )
				{
					OpenRA.Game.Exit();
					return true;
				}
				return false;

			default:
				return false;
			}
		}

		public void Present()
		{
			Sdl.SDL_GL_SwapBuffers();
		}

		public void PumpInput( IInputHandler inputHandler )
		{
			Game.HasInputFocus = 0 != ( Sdl.SDL_GetAppState() & Sdl.SDL_APPINPUTFOCUS );

			var mods = MakeModifiers( Sdl.SDL_GetModState() );
			inputHandler.ModifierKeys( mods );
			MouseInput? pendingMotion = null;

			Sdl.SDL_Event e;
			while( Sdl.SDL_PollEvent( out e ) != 0 )
			{
				switch( e.type )
				{
				case Sdl.SDL_QUIT:
					OpenRA.Game.Exit();
					break;

				case Sdl.SDL_MOUSEBUTTONDOWN:
					{
						if( pendingMotion != null )
						{
							inputHandler.OnMouseInput( pendingMotion.Value );
							pendingMotion = null;
						}

						var button = MakeButton( e.button.button );
						lastButtonBits |= button;

						inputHandler.OnMouseInput( new MouseInput(
							MouseInputEvent.Down, button, new int2( e.button.x, e.button.y ), mods ) );
					} break;

				case Sdl.SDL_MOUSEBUTTONUP:
					{
						if( pendingMotion != null )
						{
							inputHandler.OnMouseInput( pendingMotion.Value );
							pendingMotion = null;
						}

						var button = MakeButton( e.button.button );
						lastButtonBits &= ~button;

						inputHandler.OnMouseInput( new MouseInput(
							MouseInputEvent.Up, button, new int2( e.button.x, e.button.y ), mods ) );
					} break;

				case Sdl.SDL_MOUSEMOTION:
					{
						pendingMotion = new MouseInput(
							MouseInputEvent.Move,
							lastButtonBits,
							new int2( e.motion.x, e.motion.y ),
							mods );
					} break;

				case Sdl.SDL_KEYDOWN:
					{
						var keyEvent = new KeyInput
						{
							Event = KeyInputEvent.Down,
							Modifiers = mods,
							UnicodeChar = (char)e.key.keysym.unicode,
							KeyName = Sdl.SDL_GetKeyName( e.key.keysym.sym ),
							VirtKey = e.key.keysym.sym
						};

						if( !HandleSpecialKey( keyEvent ) )
							inputHandler.OnKeyInput( keyEvent );
					} break;

				case Sdl.SDL_KEYUP:
					{
						var keyEvent = new KeyInput
						{
							Event = KeyInputEvent.Up,
							Modifiers = mods,
							UnicodeChar = (char)e.key.keysym.unicode,
							KeyName = Sdl.SDL_GetKeyName( e.key.keysym.sym ),
							VirtKey = e.key.keysym.sym
						};

						inputHandler.OnKeyInput( keyEvent );
					} break;
				}
			}

			if( pendingMotion != null )
			{
				inputHandler.OnMouseInput( pendingMotion.Value );
				pendingMotion = null;
			}

			ErrorHandler.CheckGlError();
		}

		public void DrawPrimitives( PrimitiveType pt, int firstVertex, int numVertices )
		{
			Gl.glDrawArrays( ModeFromPrimitiveType( pt ), firstVertex, numVertices );
			ErrorHandler.CheckGlError();
		}

		static int ModeFromPrimitiveType( PrimitiveType pt )
		{
			switch( pt )
			{
			case PrimitiveType.PointList: return Gl.GL_POINTS;
			case PrimitiveType.LineList: return Gl.GL_LINES;
			case PrimitiveType.TriangleList: return Gl.GL_TRIANGLES;
			case PrimitiveType.QuadList: return Gl.GL_QUADS;
			}
			throw new NotImplementedException();
		}

		public IVertexBuffer<Vertex> CreateVertexBuffer( int size ) { return new VertexBuffer<Vertex>( size ); }
		public ITexture CreateTexture() { return new Texture(); }
		public ITexture CreateTexture( Bitmap bitmap ) { return new Texture( bitmap ); }
		public IShader CreateShader( string name ) { return new Shader( this, name ); }

		public int GpuMemoryUsed { get { return 0; } }
	}
}
