﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information. 

#if !NO_REMOTING
using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.PlatformServices;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace ReactiveTests.Tests
{
    
    [Serializable]
    public class DefaultConcurrencyAbstractionLayerTest
    {
        private AppDomain _domain;

        public DefaultConcurrencyAbstractionLayerTest()
        {
            if (_domain == null)
            {
                var cur = AppDomain.CurrentDomain.BaseDirectory;
                var sub = Path.Combine(cur, "NoCAL");

                if (!Directory.Exists(sub))
                {
                    Directory.CreateDirectory(sub);
                }

                //
                // We want to replace the files to make the debugging experience
                // better. If the directory already exists, a recompilation does
                // not get rid of it. At some point, we should revisit this whole
                // directory creation during the test run and try to do away with
                // it by making it part of the build process.
                //

                foreach (var file in Directory.GetFiles(cur))
                {
                    var fn = Path.GetFileName(file);
                    if (!file.Contains("PlatformServices"))
                    {
                        var dest = Path.Combine(sub, fn);

                        if (File.Exists(dest))
                        {
                            try
                            {
                                File.Delete(dest);
                            }
                            catch (UnauthorizedAccessException)
                            {
                                //
                                // File in use; expected after first pass.
                                //
                            }
                        }

                        if (!File.Exists(dest))
                        {
                            File.Copy(Path.Combine(cur, fn), Path.Combine(sub, fn));
                        }
                    }
                }

                _domain = AppDomain.CreateDomain("Default_CAL", null, new AppDomainSetup { ApplicationBase = sub });
            }
        }

        private void Run(CrossAppDomainDelegate a)
        {
            _domain.DoCallBack(a);
        }

        [Fact]
        public void Sleep()
        {
            var ran = new MarshalByRefCell<bool>();
            _domain.SetData("state", ran);

            Run(() =>
            {
                Scheduler.Immediate.Schedule(TimeSpan.FromMilliseconds(1), () =>
                {
                    var state = (MarshalByRefCell<bool>)_domain.GetData("state");
                    state.Value = true;
                });
            });

            Assert.True(ran.Value);
        }

        [Fact]
        public void QueueUserWorkItem()
        {
            var e = new MarshalByRefCell<ManualResetEvent> { Value = new ManualResetEvent(false) };
            _domain.SetData("state", e);

            Run(() =>
            {
                Scheduler.Default.Schedule(() =>
                {
                    var state = (MarshalByRefCell<ManualResetEvent>)_domain.GetData("state");
                    state.Value.Set();
                });
            });

            e.Value.WaitOne();
        }

        [Fact]
        public void StartTimer()
        {
            var e = new MarshalByRefCell<ManualResetEvent> { Value = new ManualResetEvent(false) };
            _domain.SetData("state", e);

            Run(() =>
            {
                Scheduler.Default.Schedule(TimeSpan.FromMilliseconds(10), () =>
                {
                    var state = (MarshalByRefCell<ManualResetEvent>)_domain.GetData("state");
                    state.Value.Set();
                });
            });

            e.Value.WaitOne();
        }

        [Fact]
        public void StartTimer_Cancel()
        {
            Run(StartTimer_Cancel_Callback);
        }

        private static void StartTimer_Cancel_Callback()
        {
            Scheduler.Default.Schedule(TimeSpan.FromSeconds(60), () =>
            {
                throw new InvalidOperationException("This shouldn't have happened!");
            }).Dispose();
        }

        [Fact]
        public void StartPeriodicTimer()
        {
            var e = new MarshalByRefCell<ManualResetEvent> { Value = new ManualResetEvent(false) };
            _domain.SetData("state", e);

            Run(() =>
            {
                var n = 0;

                Scheduler.Default.SchedulePeriodic(TimeSpan.FromMilliseconds(10), () =>
                {
                    var state = (MarshalByRefCell<ManualResetEvent>)_domain.GetData("state");

                    if (n++ == 10)
                        state.Value.Set();
                });
            });

            e.Value.WaitOne();
        }

        [Fact]
        public void StartPeriodicTimer_Cancel()
        {
            Run(StartPeriodicTimer_Cancel_Callback);
        }

        private static void StartPeriodicTimer_Cancel_Callback()
        {
            Scheduler.Default.SchedulePeriodic(TimeSpan.FromSeconds(60), () =>
            {
                throw new InvalidOperationException("This shouldn't have happened!");
            }).Dispose();
        }

        [Fact]
        public void StartPeriodicTimer_Fast()
        {
            var e = new MarshalByRefCell<ManualResetEvent> { Value = new ManualResetEvent(false) };
            _domain.SetData("state", e);

            Run(() =>
            {
                var n = 0;

                Scheduler.Default.SchedulePeriodic(TimeSpan.Zero, () =>
                {
                    var state = (MarshalByRefCell<ManualResetEvent>)_domain.GetData("state");

                    if (n++ == 10)
                        state.Value.Set();
                });
            });

            e.Value.WaitOne();
        }

        [Fact]
        public void StartPeriodicTimer_Fast_Cancel()
        {
            var e = new MarshalByRefCell<ManualResetEvent> { Value = new ManualResetEvent(false) };
            _domain.SetData("set_cancel", e);

            Run(() =>
            {
                var n = 0;

                var hasAtLeastOneValue = new ManualResetEvent(false);

                var schedule = Scheduler.Default.SchedulePeriodic(TimeSpan.Zero, () =>
                {
                    _domain.SetData("value", n++);
                    hasAtLeastOneValue.Set();
                });

                _domain.SetData("cancel", new MarshalByRefAction(schedule.Dispose));

                hasAtLeastOneValue.WaitOne();

                var setCancel = (MarshalByRefCell<ManualResetEvent>)_domain.GetData("set_cancel");

                setCancel.Value.Set();
            });

            e.Value.WaitOne();

            var value = (int)_domain.GetData("value");

            var cancel = (MarshalByRefAction)_domain.GetData("cancel");
            cancel.Invoke();

            Thread.Sleep(TimeSpan.FromMilliseconds(50));

            var newValue = (int)_domain.GetData("value");

            Assert.True(newValue >= value);

            Thread.Sleep(TimeSpan.FromMilliseconds(50));

            value = (int)_domain.GetData("value");

            Assert.Equal(newValue, value);
        }

        [Fact]
        public void CreateThread()
        {
            var e = new MarshalByRefCell<ManualResetEvent> { Value = new ManualResetEvent(false) };
            _domain.SetData("state", e);

            var r = new MarshalByRefCell<string> { Value = "" };
            _domain.SetData("res", r);

            Run(() =>
            {
                var state = (MarshalByRefCell<ManualResetEvent>)_domain.GetData("state");
                var res = (MarshalByRefCell<string>)_domain.GetData("res");

                var svc = (IServiceProvider)Scheduler.Default;

                var per = (ISchedulerPeriodic)svc.GetService(typeof(ISchedulerPeriodic));
                if (per == null)
                {
                    res.Value = "Failed to get ISchedulerPeriodic.";
                    state.Value.Set();
                    return;
                }

                var slr = (ISchedulerLongRunning)svc.GetService(typeof(ISchedulerLongRunning));
                if (slr == null)
                {
                    res.Value = "Failed to get ISchedulerLongRunning.";
                    state.Value.Set();
                    return;
                }

                var success = false;
                try
                {
                    slr.ScheduleLongRunning(42, null);
                }
                catch (ArgumentNullException)
                {
                    success = true;
                }

                if (!success)
                {
                    res.Value = "Failed null check ScheduleLongRunning.";
                    state.Value.Set();
                    return;
                }

                state.Value.Set();
#if !NO_THREAD
                var w = new ManualResetEvent(false);

                var d = slr.ScheduleLongRunning(cancel =>
                {
                    while (!cancel.IsDisposed)
                        ;

                    w.Set();
                });

                Thread.Sleep(50);
                d.Dispose();
                w.WaitOne();
#else
                state.Value.Set();
#endif
            });

            e.Value.WaitOne();
            Assert.True(string.IsNullOrEmpty(r.Value));
        }

#if !NO_TPL
        [Fact]
        public void Cant_Locate_Scheduler()
        {
            if (!Utils.IsRunningWithPortableLibraryBinaries())
            {
                Cant_Locate_Scheduler_NoPlib();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Cant_Locate_Scheduler_NoPlib()
        {
            var e = new MarshalByRefCell<Exception>();
            _domain.SetData("state", e);

            Run(() =>
            {
                var state = (MarshalByRefCell<Exception>)_domain.GetData("state");
                try
                {
                    Scheduler.TaskPool.Schedule(() => { });
                }
                catch (Exception ex)
                {
                    state.Value = ex;
                }
            });

            Assert.True(e.Value != null && e.Value is NotSupportedException);
        }
#endif

#if !NO_PERF && !NO_STOPWATCH
        [Fact]
        public void Stopwatch()
        {
            var e = new MarshalByRefCell<bool>();
            _domain.SetData("state", e);

            Run(() =>
            {
                var state = (MarshalByRefCell<bool>)_domain.GetData("state");

                var sw = Scheduler.Default.StartStopwatch();

                var fst = sw.Elapsed;
                Thread.Sleep(100);
                var snd = sw.Elapsed;

                state.Value = snd > fst;
            });

            Assert.True(e.Value);
        }
#endif

        [Fact]
        public void EnsureLoaded()
        {
            Assert.True(EnlightenmentProvider.EnsureLoaded());
        }
    }

    public class MarshalByRefCell<T> : MarshalByRefObject
    {
        public T Value;
    }

    public class MarshalByRefAction : MarshalByRefObject
    {
        private readonly Action _action;

        public MarshalByRefAction(Action action)
        {
            _action = action;
        }

        public void Invoke()
        {
            _action();
        }
    }
}
#endif