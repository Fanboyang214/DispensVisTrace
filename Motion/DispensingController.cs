using Core.Motion;
using Device_Link_LTSMC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Motion
{
    public class DispensingController : IDispensingContoller
    {
        ControllerConfig _config;

        public void Initialize()
        {
            LTSMC.smc_set_vector_profile_unit(_config.ConnectNo,_config.Crd,0,100,0.2,0.2,0);
            LTSMC.smc_set_vector_s_profile(_config.ConnectNo, _config.Crd, 0, 0.15);
            
        }
        public bool AxisMoveAbs(short axisNo, double dist)
        {
            return LTSMC.smc_pmove_unit(_config.ConnectNo, axisNo, dist,1) == 0x0;
        }

        public bool OpenContiList()
        {
            return LTSMC.smc_conti_open_list(_config.ConnectNo, _config.Crd, 2, [0, 1]) == 0x0;

        }

      
        public bool ContiUnit(double[] posList)
        {
            return LTSMC.smc_conti_line_unit(_config.ConnectNo, _config.Crd, 2, [0,1],posList,1,0) == 0x0;
        }

        public bool StartConti()
        {
            return LTSMC.smc_conti_start_list(_config.ConnectNo, _config.Crd)==0x0;
        }

        public bool EmergencyStop()
        {
            return LTSMC.smc_emg_stop(_config.ConnectNo) == 0x0;
        }

        public bool HomeAllAxis()
        {
            return LTSMC.smc_home_move()
        }

        public bool HomeAxis(short axisNo)
        {
            throw new NotImplementedException();
        }

        public bool LineUnit(ushort[] axisList, double[] dist)
        {
           
            return  LTSMC.smc_line_unit(_config.ConnectNo, axisList, dist, 1) == 0x0;
         

        }

        public bool MoveAbs(ushort[] axisList, double[] dist)
        {
            return LineUnit(axisList, dist);
        }

        public bool SetArcUnit()
        {
            throw new NotImplementedException();
        }

        public bool StopAll(short axisNo)
        {
            throw new NotImplementedException();
        }

        public bool StopAxis(short axisNo)
        {
            throw new NotImplementedException();
        }
    }
}
