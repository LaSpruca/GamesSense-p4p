using System;
using System.Runtime.CompilerServices;
using UnityEngine.Networking;

namespace HapticAI.Net
{
    /// <summary>
    /// Lets you <c>await request.SendWebRequest()</c> directly. Unity resumes the continuation
    /// on the main thread (via UnitySynchronizationContext), so anything the pipeline does after
    /// an await — including OVR haptics playback in the glue layer — stays main-thread safe.
    /// </summary>
    public readonly struct UnityWebRequestAwaiter : INotifyCompletion
    {
        readonly UnityWebRequestAsyncOperation _op;

        public UnityWebRequestAwaiter(UnityWebRequestAsyncOperation op) => _op = op;

        public bool IsCompleted => _op.isDone;

        public void OnCompleted(Action continuation)
        {
            if (_op.isDone) continuation();
            else _op.completed += _ => continuation();
        }

        public void GetResult() { /* result is read off the UnityWebRequest itself */ }
    }

    public static class UnityWebRequestExtensions
    {
        public static UnityWebRequestAwaiter GetAwaiter(this UnityWebRequestAsyncOperation op)
            => new UnityWebRequestAwaiter(op);
    }
}
