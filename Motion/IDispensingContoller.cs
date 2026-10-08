using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Motion
{
    public interface IDispensingContoller
    {

        public bool MoveAbs(ushort[] axisList, double[] dist);

        public bool AxisMoveAbs(short axisNo, double dist);

        public bool HomeAllAxis();

        public bool HomeAxis(short axisNo);

        public bool StopAll(short axisNo);

        public bool StopAxis(short axisNo);

        public bool LineUnit(ushort[] axisList, double[] dist);

        public bool ContiUnit(double[] posList);

        public bool SetArcUnit();

        public bool EmergencyStop();




    }
}
