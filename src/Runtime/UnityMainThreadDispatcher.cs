// UnityMainThreadDispatcher.cs
// 백그라운드 스레드(Process.Exited 등)에서 Unity 메인 스레드 작업 실행

using System;
using System.Collections.Generic;
using UnityEngine;

namespace VirtualDresser.Runtime
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static readonly Queue<Action> _queue = new();
        private static UnityMainThreadDispatcher _instance;

        public static void Enqueue(Action action)
        {
            lock (_queue) _queue.Enqueue(action);
        }

        private void Awake()
        {
            if (_instance != null) { Destroy(gameObject); return; }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            while (true)
            {
                Action action;
                lock (_queue)
                {
                    if (_queue.Count == 0) break;
                    action = _queue.Dequeue();
                }
                action?.Invoke();
            }
        }
    }
}
