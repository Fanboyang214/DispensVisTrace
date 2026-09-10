using Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Vision
{
    public interface IInspectEngine:IDisposable
    {
        Task InitializeAsync(InspectConfig config);

        void Inspect();
    }
}
