using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

/********************************** ZMOTION CAD 动态库  ************************************************
**--------------文件信息--------------------------------------------------------------------------------
**文件名: ZMotionCad.cs
**创建人: 梁
**时间: 20201023
**描述: ZMOTION CAD  外部接口函数

**------------修订历史记录----------------------------------------------------------------------------
** 修改人: wjs
** 版  本: ZMotion CAD V3.1.0
** 日　期: 2024.7.15
** 描　述: Array库合并到ZMotionEx.dll，更新接口文件，文件重新命名为ZMotionCadEx.cs
**
**------------------------------------------------------------------------------------------------------
********************************** ZMOTION CAD 动态库  ************************************************/

namespace ZmotionCadDll
{
    public class ZmotionCad
    {

        public const int ZCAD_ITEMTYPE_VECT = 2000;
        public const int ZCAD_ITEMTYPE_VECTPoint = 2001;
        public const int ZCAD_ITEMTYPE_VECTLine = 2002;
        public const int ZCAD_ITEMTYPE_VECTArc = 2004;

        public struct Struct_ZCad_Array
        {
            [MarshalAs(UnmanagedType.I4)]
            public int m_nGroupNum;    //图层
            [MarshalAs(UnmanagedType.I4)]
            public int m_nItemtype;     //类型

            [MarshalAs(UnmanagedType.I4)]
            public int m_nInVectFrist;  //在曲线中是否为第一个
            [MarshalAs(UnmanagedType.I4)]
            public int m_nEmptyMove;    //是否为空移（点类型使用，表示为空移点）
            [MarshalAs(UnmanagedType.I4)]
            public int m_nIfCloseLine;  //是否闭合线
            [MarshalAs(UnmanagedType.I4)]
            public int m_nChoose;       //是否选中
            [MarshalAs(UnmanagedType.I4)]
            public int m_nGroup;        //群组号

            [MarshalAs(UnmanagedType.I4)]
            public int m_Param1;        // 预留特殊参数
            [MarshalAs(UnmanagedType.I4)]
            public int m_Param2;        // 预留

            public double x1;
            public double y1;
            public double z1;           //（圆弧时表示起始角度）

            public double x2;
            public double y2;
            public double z2;           //（圆弧时表示旋转角度）

            public double m_dGetStartX;
            public double m_dGetStartY; //提取数组获得的图形起点位置（m_nInVectFrist=1时有效）

        };

        /*************************************************************
        Description:    // 新建一个对象并插入到图层的末尾
        Input:          //struct_Vect  新建的对象
        Output:         //
        Return:         //错误码
        int __stdcall ZMotionCadArray_NewOne(Struct_ZCad_Array struct_NewVect);
        *************************************************************/
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_NewOne", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_NewOne(Struct_ZCad_Array struct_NewVect);

        /*************************************************************
        Description:    // 删除指定对象
        Input:          //nDelVect  需要删除的对象的序号
        Output:         //
        Return:         //错误码
        int __stdcall ZMotionCadArray_DelOne(int nDelVect);
        *************************************************************/
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_DelOne", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_DelOne(int nDelVect);

        /*************************************************************
        Description:    // 删除所有对象
        Input:          //
        Output:         //
        Return:         //错误码
        int __stdcall ZMotionCadArray_DelAll();
        *************************************************************/
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_DelAll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_DelAll();

        /*************************************************************
        Description:    //导入图形文件(支持dxf、plt、ai、dst)
        Input:          //
			        lpszFileFullPathname 路径和文件名
			        duUnit  PLT的比例
			        Option   预留， 缺省都转换为seg
			        refDistance    转换时参考精度
        Output:         //
        Return:         //错误码
        int __stdcall ZMotionCadArray_ImportVectGraph(LPCTSTR lpszFileFullPathname, double duUnit, int Option, double refDistance);
        *************************************************************/
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_ImportVectGraph", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_ImportVectGraph(string lpszFileFullPathname, double duUnit, int Option, double refDistance);

        /*************************************************************
        Description:    // 范围   坐标方向是向上为正
        Input:          //
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32  __stdcall ZMotionCadArray_GetRange(float *pLeft, float *pBottom, float *pWdith, float *pHeight, double refDistance);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_GetRange", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_GetRange(ref float pLeft, ref float pBottom, ref float pWdith, ref float pHeight, double refDistance);

        /*************************************************************
        Description:    // 拷贝数组
        Input:          // nStructNum  拷贝的长度
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32  __stdcall ZMotionCadArray_CopyChain(Struct_ZCad_Array *pOldArray, Struct_ZCad_Array *pNewArray, int nStructNum);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_CopyChain", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_CopyChain(ref Struct_ZCad_Array pOldArray, ref Struct_ZCad_Array pNewArray, int nStructNum);

        /*************************************************************
        Description:    // 移动对象
        Input:          // m_x         x方向移动的距离
                        // m_y         y方向移动的距离
				        // nMoveVect   需要移动的对象的序号，-1为移动所有
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32  __stdcall ZMotionCadArray_Move(double m_x, double m_y, int nMoveVect);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_Move", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_Move(double m_x, double m_y, int nMoveVect);


        /*************************************************************
        Description:    // 缩放对象
        Input:          //nScaleVect   需要缩放的对象的序号，-1为缩放所有
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32  __stdcall ZMotionCadArray_Scale(float scaleX, float scaleY, float pointx, float pointy, int nScaleVect);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_Scale", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_Scale(float scaleX, float scaleY, float pointx, float pointy, int nScaleVect);

        /*************************************************************
        Description:    // 插入对象
        Input:          // nArrayNum    输入的数组数量
                        // nInsertNo    插入的位置
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32  __stdcall ZMotionCadArray_ItemInsert(Struct_ZCad_Array *struct_NewVect, int nArrayNum, int nInsertNo);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_ItemInsert", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_ItemInsert(ref Struct_ZCad_Array struct_NewVect, int nArrayNum, int nInsertNo);

        /*************************************************************
        Description:    // 修改对象
        Input:          // nArrayNum    输入的数组数量
                        // nInsertNo    修改的对象位置
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32  __stdcall ZMotionCadArray_ItemModify(Struct_ZCad_Array *struct_NewVect, int nArrayNum, int nModifyNo);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_ItemModify", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_ItemModify(ref Struct_ZCad_Array struct_NewVect, int nArrayNum, int nModifyNo);

        /*************************************************************
        Description:    // 提取当前图形数组长度
        Input:          //nStructNum  数组的数量
        Output:         //
        Return:         //错误码
        *************************************************************/
        //int __stdcall ZMotionCadArray_GetVectNum(int *nStructNum);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_GetVectNum", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_GetVectNum(ref int nStructNum);

        /*************************************************************
        Description:    // 提取当前图形数组
        Input:          //struct_Array  提取的数组
                        //nStructNum    数组的数量
        Output:         //
        Return:         //错误码
        *************************************************************/
        //int __stdcall ZMotionCadArray_GetVectArray(Struct_ZCad_Array *struct_Array, int nStructNum);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_GetVectArray", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_GetVectArray(ref Struct_ZCad_Array struct_Array, int nStructNum);

        /************************************************************************/
        /* 以下为高级功能                                                       */
        /************************************************************************/

        /*************************************************************
        Description:    //与控制器建立链接，成功后解锁高级功能
        Input:          //IP地址，字符串的方式输入
        Output:         //控制器句柄
        Return:         //错误码
        int32 __stdcall ZMotionCadArray_OpenEth(char *ipaddr, ZMC_HANDLE *pHandle);
        *************************************************************/
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_OpenEth", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_OpenEth(string ipaddr, out IntPtr phandle);

        /*************************************************************
        Description:    //点选
        Input:          //dCurXPos	鼠标的X坐标
				        //dCurYPos	鼠标的Y坐标
				        //dDist		点击允许误差
				        //bMore		多选
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_SelOne(double dCurXPos, double dCurYPos, double dDist, bool bMore);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_SelOne", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_SelOne(double dCurXPos, double dCurYPos, double dDist, bool bMore);

        /*************************************************************
        Description:    //从左到右框选
        Input:          //dPrevXPos	第一点的X坐标
				        //dPrevYPos	第一点的Y坐标
				        //dCurXPos	第二点的X坐标
				        //dCurYPos	第二点的Y坐标
				        //bMore		多选
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_SelLeftToRight(double dPrevXPos, double dPrevYPos, double dCurXPos, double dCurYPos, bool bMore);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_SelLeftToRight", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_SelLeftToRight(double dPrevXPos, double dPrevYPos, double dCurXPos, double dCurYPos, bool bMore);

        /*************************************************************
        Description:    //从右到左框选
        Input:          //dPrevXPos	第一点的X坐标
				        //dPrevYPos	第一点的Y坐标
				        //dCurXPos	第二点的X坐标
				        //dCurYPos	第二点的Y坐标
				        //bMore		多选
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_SelRightToLeft(double dPrevXPos, double dPrevYPos, double dCurXPos, double dCurYPos, bool bMore);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_SelRightToLeft", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_SelRightToLeft(double dPrevXPos, double dPrevYPos, double dCurXPos, double dCurYPos, bool bMore);

        /*************************************************************
        Description:    //按图层选择
        Input:          //nGroupNo	图层号
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_SelVectGroup(int nGroupNo);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_SelVectGroup", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_SelVectGroup(int nGroupNo);

        /*************************************************************
        Description:    //选中部分更改图层  
        Input:          //nGroupNo	图层号
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_ChangeVectGroup(int nGroupNo);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_ChangeVectGroup", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_ChangeVectGroup(int nGroupNo);

        /*************************************************************
        Description:    //区分是否为封闭图形
        Input:          //bChoose	是否仅处理选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_IfCloseVect(bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_IfCloseVect", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_IfCloseVect(bool bChoose);

        /*************************************************************
        Description:    //去除极小图形
        Input:          //dXSize	X方向的尺寸
				        //dYSize	Y方向的尺寸
				        //bChoose	是否仅处理选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_DelLitSeg(double dXSize, double dYSize, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_DelLitSeg", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_DelLitSeg(double dXSize, double dYSize, bool bChoose);

        /*************************************************************
        Description:    //曲线平滑
        Input:          //refDistance	平滑精度 范围0.01~0.5
				        //bChoose		是否仅处理选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_CurveSmooth(double refDistance, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_CurveSmooth", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_CurveSmooth(double refDistance, bool bChoose);

        /*************************************************************
        Description:    //去除重复线
        Input:          //bChoose	是否仅处理选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_DelSameSeg(bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_DelSameSeg", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_DelSameSeg(bool bChoose);

        /*************************************************************
        Description:    //合并相连线
        Input:          //dSize		合并的最大距离
				        //bChoose	是否仅处理选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_MergeSeg(double dSize, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_MergeSeg", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_MergeSeg(double dSize, bool bChoose);

        /*************************************************************
        Description:    //分割曲线
        Input:          //dCurXPos	鼠标的X坐标
				        //dCurYPos	鼠标的Y坐标
				        //dDist		点击允许误差
				        //bSplit	分割/检测有效区
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_SplitVect(double dCurXPos, double dCurYPos, double dDist, bool bSplit);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_SplitVect", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_SplitVect(double dCurXPos, double dCurYPos, double dDist, bool bSplit);

        /*************************************************************
        Description:    //更改图形起始点
        Input:          //dCurXPos	鼠标的X坐标
				        //dCurYPos	鼠标的Y坐标
				        //dDist		点击允许误差
				        //bChange	更改起点/检测有效区
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_ChangStartPt(double dCurXPos, double dCurYPos, double dDist, bool bChange);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_ChangStartPt", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_ChangStartPt(double dCurXPos, double dCurYPos, double dDist, bool bChange);

        /*************************************************************
        Description:    //优化空移
        Input:          //refDistance	参考精度 范围0.01~0.5
				        //bChange		是否仅处理选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_OptimizeAbs(double refDistance, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_OptimizeAbs", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_OptimizeAbs(double refDistance, bool bChoose);

        /*************************************************************
        Description:    //反向（选中生效）
        Input:          //
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_Reverse();
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_Reverse", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_Reverse();



        /*************************************************************
        Description:    //网格排序
        Input:          //dXSize	网格宽度
				        //dYSize	网格长度
				        //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_GridsSort(double dXSize, double dYSize, bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_GridsSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_GridsSort(double dXSize, double dYSize, bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //局部最小空移排序
        Input:          //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_MinAbsSort(bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_MinAbsSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_MinAbsSort(bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //小图优先排序
        Input:          //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_MinVectSort(bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_MinVectSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_MinVectSort(bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //从内到外排序
        Input:          //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_InSort(bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_InSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_InSort(bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //从左到右排序
        Input:          //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_LeftSort(bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_LeftSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_LeftSort(bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //从右到左排序
        Input:          //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_RightSort(bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_RightSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_RightSort(bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //从上到下排序
        Input:          //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_TopSort(bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_TopSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_TopSort(bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //从下到上排序
        Input:          //bExChange	是否允许反向
				        //bChange	是否仅排选中图形
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_BottomSort(bool bExChange, bool bChoose);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_BottomSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_BottomSort(bool bExChange, bool bChoose);

        /*************************************************************
        Description:    //手动排序（选中生效）
        Input:          //nRankType	手动排序的方式（1=前移；2=后移；3=最前；4=最后）
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_HandSort(int nSortType);
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_HandSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_HandSort(int nSortType);

        /*************************************************************
        Description:    //反序（选中生效）
        Input:          //
        Output:         //
        Return:         //错误码
        *************************************************************/
        //uint32 __stdcall ZMotionCadArray_ReSort();
        [DllImport("ZmotionCadEx.dll", EntryPoint = "ZMotionCadArray_ReSort", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
        public static extern Int32 ZMotionCadArray_ReSort();

    }
}
