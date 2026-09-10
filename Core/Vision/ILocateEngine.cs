using Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Core.Vision
{
    public interface ILocateEngine:IDisposable
    {
        Task InitializeAsync(LocateConfig config);

        Task LocateAsync(InspectionImage image,CancellationToken ct);
    }
}
