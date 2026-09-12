using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Acquisition
{
    public interface ICameraDriver
    {
        bool IsOpen { get; }

        void Open();

        void Close();

        void StartGrab();

        void StopGrab();

        byte[] GrabData(int timeoutMs = 2000);

        void SetTriggerMode(bool isSoftwareTrigger);

    }
}
