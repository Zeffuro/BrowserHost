﻿using BrowserHost.Common;
using CefSharp;
using CefSharp.Structs;
using SharedMemory;
using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace BrowserHost.Renderer.RenderHandlers
{
	class BitmapBufferRenderHandler : BaseRenderHandler
	{
		// CEF buffers are 32-bit BGRA
		private const byte bytesPerPixel = 4;

		public string BitmapBufferName { get { return bitmapBuffer.Name; } }
		public string FrameInfoBufferName { get { return frameInfoBuffer.Name; } }

		private BufferReadWrite bitmapBuffer;
		private CircularBuffer frameInfoBuffer;
		private System.Drawing.Size size;

		private ConcurrentBag<SharedBuffer> obsoleteBuffers = new ConcurrentBag<SharedBuffer>();

		public BitmapBufferRenderHandler(System.Drawing.Size size)
		{
			this.size = size;

			BuildBitmapBuffer(size);

			// TODO: Sane size.
			var frameInfoBuffername = $"BrowserHostFrameInfoBuffer{Guid.NewGuid()}";
			frameInfoBuffer = new CircularBuffer(frameInfoBuffername, 5, 1024 /* 1K */);
		}

		public override void Dispose()
		{
			frameInfoBuffer.Dispose();
		}

		public override void Resize(System.Drawing.Size newSize)
		{
			// If new size is same as current, noop
			if (
				newSize.Width == size.Width &&
				newSize.Height == size.Height
			) {
				return;
			}

			// Build new buffers & set up on instance
			size = newSize;
			BuildBitmapBuffer(newSize);
		}

		protected override byte GetAlphaAt(int x, int y)
		{
			// TODO
			return 255;
		}

		public override Rect GetViewRect()
		{
			return new Rect(0, 0, size.Width, size.Height);
		}

		public override void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
		{
			// TODO: Popups
			if (type != PaintElementType.View) { return; }

			// If the paint size does not match our buffer size, we're likely resizing and paint hasn't caught up. Noop.
			if (width != size.Width && height != size.Height) { return; }

			// TODO: Only write dirty rect
			var length = bytesPerPixel * width * height;
			var frame = new BitmapFrame()
			{
				Length = length,
				Width = width,
				Height = height,
				DirtyX = dirtyRect.X,
				DirtyY = dirtyRect.Y,
				DirtyWidth = dirtyRect.Width,
				DirtyHeight = dirtyRect.Height,
			};

			WriteToBuffers(frame, buffer);

			// Render is complete, clean up obsolete buffers
			var obsoleteBuffers = this.obsoleteBuffers;
			this.obsoleteBuffers = new ConcurrentBag<SharedBuffer>();
			foreach (var toDispose in obsoleteBuffers) { toDispose.Dispose(); }
		}

		public override void OnPopupShow(bool show)
		{
			// TODO
		}

		public override void OnPopupSize(Rect rect)
		{
			// TODO
		}

		private void BuildBitmapBuffer(System.Drawing.Size size)
		{
			var oldBitmapBuffer = bitmapBuffer;

			var bitmapBufferName = $"BrowserHostBitmapBuffer{Guid.NewGuid()}";
			bitmapBuffer = new BufferReadWrite(bitmapBufferName, size.Width * size.Height * bytesPerPixel);

			// Mark the old buffer for disposal
			if (oldBitmapBuffer != null) { obsoleteBuffers.Add(oldBitmapBuffer); }
		}

		// There's a race condition wherein a resize from the plugin causes the browser to resize it's output buffer
		// during writing it out to the IPC, which causes an access violation. Rather than trying to prevent the race like
		// a sane developer, I'm just catching the error and nooping it - a dropped frame isn't a big issue.
		[HandleProcessCorruptedStateExceptions]
		private void WriteToBuffers(BitmapFrame frame, IntPtr buffer)
		{
			// Not using read/write locks because I'm a cowboy (and there seems to be a race cond in the locking mechanism)
			try
			{
				WriteDirtyRect(frame, buffer);
				frameInfoBuffer.Write(ref frame);
			}
			catch (AccessViolationException e)
			{
				Console.WriteLine($"Error writing to buffer, nooping frame on {bitmapBuffer.Name}: {e.Message}");
			}
		}

		private void WriteDirtyRect(BitmapFrame frame, IntPtr buffer)
		{
			// Write each row as a dirty stripe
			for (var row = frame.DirtyY; row < frame.DirtyY + frame.DirtyHeight; row++)
			{
				var position = (row * frame.Width * bytesPerPixel) + (frame.DirtyX * bytesPerPixel);
				bitmapBuffer.Write(
					buffer + position,
					frame.DirtyWidth * bytesPerPixel,
					position
				);
			}
		}
	}
}
