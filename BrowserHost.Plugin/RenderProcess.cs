﻿using BrowserHost.Common;
using Dalamud.Plugin;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Logging;

namespace BrowserHost.Plugin
{
	class RenderProcess : IDisposable
	{
		public delegate object RecieveEventHandler(object sender, UpstreamIpcRequest request);
		public event RecieveEventHandler Recieve;

		private Process process;
		private IpcBuffer<UpstreamIpcRequest, DownstreamIpcRequest> ipc;
		private bool running;

		private string keepAliveHandleName;
		private string ipcChannelName;

		public RenderProcess(int pid, string pluginDir, DependencyManager dependencyManager)
		{
			keepAliveHandleName = $"BrowserHostRendererKeepAlive{pid}";
			ipcChannelName = $"BrowserHostRendererIpcChannel{pid}";

			ipc = new IpcBuffer<UpstreamIpcRequest, DownstreamIpcRequest>(ipcChannelName, request => Recieve?.Invoke(this, request));

			var processArgs = new RenderProcessArguments()
			{
				ParentPid = pid,
				DalamudAssemblyDir = AppDomain.CurrentDomain.SetupInformation.ApplicationBase,
				CefAssemblyDir = dependencyManager.GetDependencyPathFor("cef"),
				CefCacheDir = Path.Combine(Path.GetDirectoryName(pluginDir), "cef-cache"),
				DxgiAdapterLuid = DxHandler.AdapterLuid,
				KeepAliveHandleName = keepAliveHandleName,
				IpcChannelName = ipcChannelName,
			};

			process = new Process();
			process.StartInfo = new ProcessStartInfo()
			{
				FileName = Path.Combine(pluginDir, "BrowserHost.Renderer.exe"),
				Arguments = processArgs.Serialise().Replace("\"", "\"\"\""),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			process.OutputDataReceived += (sender, args) => PluginLog.Log($"[Render]: {args.Data}");
			process.ErrorDataReceived += (sender, args) => PluginLog.LogError($"[Render]: {args.Data}");
		}

		public void Start()
		{
			if (running) { return; }
			running = true;

			process.Start();
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
		}

		public void Send(DownstreamIpcRequest request) { Send<object>(request); }

		// TODO: Option to wrap this func in an async version?
		public Task<IpcResponse<TResponse>> Send<TResponse>(DownstreamIpcRequest request)
		{
			return ipc.RemoteRequestAsync<TResponse>(request);
		}

		public void Stop()
		{
			if (!running) { return; }
			running = false;

			// Grab the handle the process is waiting on and open it up
			var handle = new EventWaitHandle(false, EventResetMode.ManualReset, keepAliveHandleName);
			handle.Set();
			handle.Dispose();

			// Give the process a sec to gracefully shut down, then kill it
			process.WaitForExit(1000);
			try { process.Kill(); }
			catch (InvalidOperationException) { }
		}

		public void Dispose()
		{
			Stop();

			process.Dispose();
			ipc.Dispose();
		}
	}
}
