﻿//#define USE_UDP_SOCKET
#if DEBUG
#define CAN_DEBUG
#endif
#define CAN_DYN_LEN
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Peak.Can.Basic;
// ReSharper disable RedundantAssignment
// ReSharper disable RedundantCast

namespace CarSimulator
{
    class CommThread
    {
        public class ConfigData
        {
            public ConfigData()
            {
                ConfigList = new List<byte>();
                ResponseOnlyList = new List<byte[]>();
                ResponseList = new List<ResponseEntry>();
            }
            public List<byte> ConfigList { get; }
            public List<byte[]> ResponseOnlyList { get; }
            public List<ResponseEntry> ResponseList { get; }
        }

        public class ResponseEntry
        {
            private int _responseIndex;

            public ResponseEntry(byte[] request, byte[] response)
            {
                Request = request;
                ResponseMultiList = new List<byte[]>();
                ResponseList = new List<byte[]> {response};
                _responseIndex = 0;
            }

            public void Reset()
            {
                _responseIndex = 0;
            }

            public byte[] Request { get; }

            public byte[] ResponseDyn
            {
                get
                {
                    if (_responseIndex >= ResponseList.Count)
                    {
                        _responseIndex = 0;
                    }
                    return ResponseList[_responseIndex++];
                }
            }

            public List<byte[]> ResponseList { get; }

            public List<byte[]> ResponseMultiList { get; }
        }

        public enum ConceptType
        {
            ConceptBwmFast,
            ConceptKwp2000Bmw,
            ConceptKwp2000S,
            ConceptDs2,
            Concept1,
            ConceptIso9141,     // Concept2
            Concept3,
        };

        public enum ResponseType
        {
            Standard,
            E61,
            E90,
        };

        private static readonly long TickResolMs = Stopwatch.Frequency/1000;
        private const byte TcpTesterAddr = 0xF4;
        private const int EnetDiagPort = 6801;
        private const int EnetControlPort = 6811;
        private volatile bool _stopThread;
        private bool _threadRunning;
        private Thread _workerThread;
        private string _comPort;
        private ConceptType _conceptType;
        private bool _adsAdapter;
        private bool _klineResponder;
        private ResponseType _responseType;
        private ConfigData _configData;
        private byte _pcanHandle;
        private long _lastCanSendTick;
        private TcpListener _tcpServerDiag;
        private TcpClient _tcpClientDiag;
        private NetworkStream _tcpClientDiagStream;
        private TcpListener _tcpServerControl;
        private TcpClient _tcpClientControl;
        private NetworkStream _tcpClientControlStream;
        private UdpClient _udpClient;
        private Socket _udpSocket;
        private readonly byte[] _udpBuffer;
        private bool _udpError;
        private long _lastTcpDiagRecTick;
        private readonly SerialPort _serialPort;
        private readonly AutoResetEvent _serialReceiveEvent;
        private readonly AutoResetEvent _pcanReceiveEvent;
        private readonly byte[] _sendData;
        private readonly byte[] _receiveData;
        private readonly byte[] _receiveDataMotorBackup;
        private int _noResponseCount;
        private readonly Stopwatch[] _timeValveWrite = new Stopwatch[4];
        private byte _mode; // 2: conveyor, 4: transport
        private int _outputs; // 0:left, 1:right, 2:down, 3:comp
        private int _axisPosPrescaler;
        private int _axisPosRaw;
        private double _axisPosFilt;
        private int _batteryVoltage;
        private int _speed;
        private int _compressorRunningTime;
        private int _idleSpeedControl;
        private readonly List<byte> _ecuErrorResetList;
        private readonly Stopwatch _timeIdleSpeedControlWrite;
        private readonly Stopwatch _receiveStopWatch;

        private const double FilterConst = 0.95;
        private const int IsoTimeout = 2000;

        // ReSharper disable InconsistentNaming
        // 0x38 EHC
        private readonly byte[] _response381802FFFF = {
            0x85, 0xF1, 0x38, 0x58, 0x01, 0x5F, 0xB4, 0x60};

        private readonly byte[] _response38175FB4 = {
            0x8F, 0xF1, 0x38, 0x57, 0x01, 0x5F, 0xB4, 0x60, 0x01, 0x28,
            0x44, 0x53, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};

        private readonly byte[] _response381A80 = {
            0x9F, 0xF1, 0x38, 0x5A, 0x80, 0x00, 0x00, 0x06, 0x78, 0x43,
            0x14, 0x04, 0x11, 0x02, 0xB0, 0x4E, 0x4C, 0x20, 0x07, 0x04,
            0x23, 0x66, 0x00, 0x10, 0x72, 0x06, 0x3F, 0x01, 0x03, 0x01,
            0x04, 0x00, 0x00, 0x00};

        private readonly byte[] _response3821C2 = {
            0x90, 0xF1, 0x38, 0x61, 0xC2, 0x03, 0xA1, 0x04, 0x01, 0x01,
            0x01, 0x00, 0x00, 0x00, 0x01, 0x01, 0x01, 0x00, 0x00, 0x8A};

        private readonly byte[] _response382230 = {
            0x80, 0xF1, 0x38, 0xA7, 0x62, 0x30, 0x00, 0x07, 0x11, 0x00,
            0x01, 0xF4, 0x01, 0xF4, 0x01, 0xF4, 0x01, 0xAC, 0x03, 0x72,
            0x06, 0xC2, 0x01, 0x26, 0x02, 0x04, 0xFF, 0xBC, 0x00, 0xBC,
            0x00, 0x52, 0x02, 0xAE, 0x01, 0xFF, 0xFF, 0x28, 0x00, 0xB8,
            0x03, 0xFF, 0xFF, 0xFF, 0xAA, 0xE0, 0x10, 0xE0, 0x0B, 0x32,
            0x04, 0x11, 0x13, 0x84, 0x03, 0xB0, 0x04, 0x10, 0x0E, 0x84,
            0x03, 0x78, 0x05, 0x0E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0x06, 0x00, 0x0A, 0x00, 0xD0, 0x20, 0x1A, 0x23, 0x32,
            0xFF, 0x3C, 0x05, 0x32, 0xFF, 0x19, 0x03, 0x18, 0x1C, 0x1E,
            0xFF, 0x40, 0x9C, 0xF7, 0x09, 0xF9, 0x07, 0xD9, 0x00, 0xD0,
            0x07, 0xFA, 0x06, 0xFC, 0x04, 0xFF, 0xFF, 0xF7, 0x09, 0xF9,
            0x07, 0xF7, 0x09, 0xF9, 0x07, 0x00, 0x00, 0x1E, 0x1E, 0xFF,
            0xFF, 0xF1, 0xFF, 0x0F, 0x00, 0x91, 0xE6, 0x6F, 0x19, 0x91,
            0xE6, 0x6F, 0x19, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0A, 0xC8,
            0x00, 0x64, 0x00, 0xFD, 0xFF, 0x64, 0x00, 0x0A, 0x01, 0x12,
            0x05, 0x12, 0x0A, 0x03, 0x64, 0x08, 0x64, 0x08, 0x64, 0x01,
            0xB4, 0x00, 0xFC, 0x04, 0xFC, 0x04, 0x01, 0xFF, 0x64, 0xDE,
            0x42};

        private readonly byte[] _response121A80 = {
            0xBC, 0xF1, 0x12, 0x5A, 0x80, 0x00, 0x00, 0x07, 0x80, 0x81,
            0x25, 0x00, 0x00, 0x00, 0x12, 0x4C, 0x50, 0x20, 0x08, 0x02,
            0x15, 0x08, 0x08, 0x02, 0x30, 0x39, 0x34, 0x37, 0x03, 0x03,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x79, 0x51, 0x46,
            0x31, 0x65, 0x57, 0x28, 0x30, 0x30, 0x38, 0x39, 0x51, 0x39,
            0x30, 0x30, 0x30, 0x38, 0x39, 0x51, 0x39, 0x30, 0x41, 0x39,
            0x34, 0x37, 0x42};

        private readonly byte[] _response121A94 = {
            0x8C, 0xF1, 0x12, 0x5A, 0x94, 0x31, 0x30, 0x33, 0x37, 0x33,
            0x38, 0x39, 0x38, 0x38, 0x32};

        private readonly byte[] _response122120 = {
            0x8C, 0xF1, 0x12, 0x61, 0x20, 0x4F, 0x5F, 0x46, 0x31, 0x52,
            0x39, 0x34, 0x37, 0x20, 0x20};

        private readonly byte[] _response12224021 = {
            0x97, 0xF1, 0x12, 0x62, 0x40, 0x21, 0x39, 0x31, 0x33, 0x32,
            0x32, 0x35, 0x30, 0x06, 0x39, 0xB9, 0x20, 0x04, 0x3C, 0x39,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00};

        private readonly byte[] _response12224022 = {
            0x80, 0xF1, 0x12, 0x4D, 0x62, 0x40, 0x22, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x36, 0x02, 0x35, 0xF7, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0x36, 0x02, 0x33, 0x47, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF,
            0xFF, 0x00, 0x88, 0x4B, 0xFF, 0x00, 0x0E, 0x00, 0xF5, 0x00,
            0x00};

        private readonly byte[] _response12224023 = {
            0xA0, 0xF1, 0x12, 0x62, 0x40, 0x23, 0x5B, 0x00, 0xAA, 0x00,
            0x00, 0x01, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x4D, 0x4F, 0xFF,
            0xFF, 0xFF, 0xFF, 0xAA, 0xB3, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x15, 0x00, 0x00, 0x00};

        private readonly byte[] _response12230000000740 = {
            0x80, 0xF1, 0x12, 0x41, 0x63, 0x40, 0x43, 0x5A, 0x34, 0x38,
            0x39, 0x36, 0x38, 0x20, 0x09, 0x01, 0x12, 0x00, 0x00, 0x07,
            0x81, 0x17, 0x42, 0x00, 0x00, 0x07, 0x81, 0x17, 0x48, 0x00,
            0x00, 0x07, 0x79, 0x67, 0x25, 0x01, 0x11, 0x11, 0x31, 0x32,
            0x33, 0x34, 0x35, 0x4C, 0x30, 0x30, 0x38, 0x39, 0x51, 0x39,
            0x30, 0x41, 0x39, 0x34, 0x37, 0x42, 0x57, 0x42, 0x41, 0x50,
            0x58, 0x31, 0x31, 0x30, 0x35, 0x30, 0xFF, 0xFF, 0xFF};

        private readonly byte[] _response12230000400740 = {
            0x80, 0xF1, 0x12, 0x41, 0x63, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};

        private readonly byte[] _response121802FFFF = {
            0x85, 0xF1, 0x12, 0x58, 0x01, 0x42, 0x32, 0x24};

        private readonly byte[] _response12174232 = {
            0xA2, 0xF1, 0x12, 0x57, 0x01, 0x42, 0x32, 0x24, 0x06, 0x00,
            0x00, 0x21, 0x28, 0x42, 0x3F, 0x1F, 0x43, 0x36, 0x2F, 0x51,
            0x64, 0x00, 0x58, 0x00, 0x93, 0x43, 0xD0, 0x1F, 0x43, 0x37,
            0x31, 0x51, 0x64, 0x00, 0x57, 0x00, 0x93};

        private readonly byte[] _response12222000 = {
            0x84, 0xF1, 0x12, 0x62, 0x20, 0x00, 0x00};

        // ReSharper disable once UnusedMember.Local
        private readonly byte[] _response12Nr = {
            0x83, 0xF1, 0x12, 0x7F, 0x1A, 0x78 };

        // Device A0 CCCA
        private readonly byte[] _responseA01802FFFF = {
            0x82, 0xF1, 0xA0, 0x58, 0x00};

        private readonly byte[] _responseA01A80p1 = {
            0x83, 0xF1, 0xA0, 0x7F, 0x1A, 0x78};

        private readonly byte[] _responseA01A80p2 = {
            0x9F, 0xF1, 0xA0, 0x5A, 0x80, 0x00, 0x00, 0x09, 0x19, 0x38,
            0x71, 0xC4, 0x0C, 0x09, 0x30, 0x4B, 0x49, 0x20, 0x07, 0x05,
            0x28, 0x10, 0x00, 0x0A, 0x94, 0x08, 0x6A, 0x01, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00};

        private readonly byte[] _responseA0222000 = {
            0x84, 0xF1, 0xA0, 0x62, 0x20, 0x00, 0x00};

        private readonly byte[] _responseA022F121 = {
            0x89, 0xF1, 0xA0, 0x62, 0xF1, 0x21, 0x00, 0x01, 0x00, 0x00, 0x07, 0xF3};

        private readonly byte[] _responseA022F120p1 = {
            0x83, 0xF1, 0xA0, 0x7F, 0x22, 0x78};

        private readonly byte[] _responseA022F120p2 = {
            0x86, 0xF1, 0xA0, 0x62, 0xF1, 0x20, 0x00, 0x03, 0x01};

        private readonly byte[] _responseA022F122p1 = {
            0x83, 0xF1, 0xA0, 0x7F, 0x22, 0x78};

        private readonly byte[] _responseA022F122p2 = {
            0x85, 0xF1, 0xA0, 0x62, 0xF1, 0x22, 0x00, 0x00};

        private readonly byte[] _responseA022F123 = {
            0x97, 0xF1, 0xA0, 0x62, 0xF1, 0x23, 0x00, 0x23, 0x29, 0xC3,
            0x3F, 0x06, 0x12, 0x7E, 0xFE, 0x00, 0x00, 0x00, 0x66, 0x01,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x01};

        private readonly byte[] _responseA022F124 = {
            0x97, 0xF1, 0xA0, 0x62, 0xF1, 0x24, 0x00, 0x23, 0x29, 0xFA,
            0x97, 0x06, 0x0F, 0xC8, 0x3B, 0x00, 0x00, 0x00, 0x5C, 0x01,
            0x00, 0x00, 0x01, 0x7E, 0xFF, 0x01};

        private readonly byte[] _responseA022F125 = {
            0x8A, 0xF1, 0xA0, 0x62, 0xF1, 0x25, 0x00, 0x00, 0xD6, 0x00,
            0x84, 0x00, 0xFB};

        private readonly byte[] _responseA022F127 = {
            0x8D, 0xF1, 0xA0, 0x62, 0xF1, 0x27, 0x00, 0xC6, 0xA4, 0x06,
            0x17, 0x20, 0x56, 0x46, 0x01, 0x01};

        private readonly byte[] _responseA022F128 = {
            0x80, 0xF1, 0xA0, 0x7E, 0x62, 0xF1, 0x28, 0x00, 0x0A, 0x0B,
            0x01, 0x11, 0x58, 0xBE, 0x93, 0x10, 0x5B, 0x01, 0x01, 0x01,
            0x03, 0x10, 0xF4, 0x84, 0x44, 0x31, 0x11, 0x01, 0x01, 0x01,
            0x0B, 0x12, 0x34, 0xC7, 0x1C, 0x1D, 0xDD, 0x01, 0x01, 0x01,
            0x0E, 0x0E, 0xD8, 0x4F, 0xA4, 0x11, 0x11, 0x01, 0x01, 0x01,
            0x12, 0x0F, 0x8C, 0x23, 0x8E, 0x0C, 0xCC, 0x01, 0x01, 0x01,
            0x13, 0x10, 0xF4, 0xA6, 0x66, 0x3B, 0x05, 0x01, 0x01, 0x01,
            0x16, 0x11, 0xBC, 0x30, 0x5B, 0x25, 0xB0, 0x01, 0x01, 0x01,
            0x1B, 0x11, 0x08, 0x67, 0xD2, 0x27, 0x1C, 0x01, 0x01, 0x01,
            0x00, 0x11, 0xF8, 0x91, 0x11, 0x12, 0x7D, 0x01, 0x01, 0x01,
            0x10, 0x00, 0x00, 0x85, 0xB0, 0x04, 0xFA, 0x01, 0x00, 0x01,
            0x1C, 0x0F, 0x8C, 0xE7, 0xD2, 0x09, 0xF4, 0x01, 0x01, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00};

        // device 0x40 (CAS car access system)
        private readonly byte[] _response401A90 = {
            0x89, 0xF1, 0x40, 0x5A, 0x90, 0x43, 0x5A, 0x34, 0x38, 0x39,
            0x36, 0x38};

        private readonly byte[] _response401802FFFF = {
            0x85, 0xF1, 0x40, 0x58, 0x01, 0xA1, 0x17, 0x21};

        // device 0x60
        private readonly byte[] _response601A80 = {
            0x9F, 0xF1, 0x60, 0x5A, 0x80, 0x00, 0x00, 0x09, 0x19, 0x61,
            0x08, 0x03, 0x07, 0x06, 0xE0, 0x53, 0x59, 0x20, 0x07, 0x05,
            0x31, 0x10, 0x00, 0x15, 0xB1, 0x89, 0x51, 0x00, 0x03, 0x10,
            0x01, 0x00, 0x00, 0x00};

        private readonly byte[] _response60210B = {
            0x86, 0xF1, 0x60, 0x61, 0x0B, 0x00, 0x02, 0x1F, 0x7E};

        private readonly byte[] _response602117 = {
            0x83, 0xF1, 0x60, 0x61, 0x17, 0x0C};

        // device 0x70
        private readonly byte[] _response70221000 = {
            0x86, 0xF1, 0x70, 0x62, 0x10, 0x00, 0xAD, 0xE8, 0xD2};

        private readonly byte[] _response701A80 = {
            0xBC, 0xF1, 0x70, 0x5A, 0x80, 0x00, 0x00, 0x09, 0x20, 0x30,
            0x82, 0x08, 0x35, 0x0D, 0x60, 0x53, 0x52, 0x20, 0x07, 0x05,
            0x29, 0x09, 0x00, 0x10, 0x70, 0x04, 0x3C, 0x00, 0x04, 0x00,
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x12, 0x94, 0x78,
            0x00, 0x05, 0x21, 0x67, 0x30, 0x30, 0x39, 0x31, 0x42, 0x35,
            0x30, 0x30, 0x30, 0x39, 0x31, 0x42, 0x35, 0x30, 0x46, 0x34,
            0x35, 0x30, 0x41};

        private readonly byte[] _response701A90 = {
            0x89, 0xF1, 0x70, 0x5A, 0x90, 0x43, 0x5A, 0x34, 0x38, 0x39,
            0x36, 0x38};

        private readonly byte[] _response70230000000712 = {
            0x93, 0xF1, 0x70, 0x63, 0x12, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0x20, 0x07, 0x05, 0x29, 0x00, 0x00, 0x09, 0x15,
            0x32, 0x73};

        private readonly byte[] _response70230000120712 = {
            0x93, 0xF1, 0x70, 0x63, 0x01, 0x43, 0x5A, 0x34, 0x38, 0x39,
            0x36, 0x38, 0x20, 0x13, 0x03, 0x05, 0x00, 0x00, 0x09, 0x20,
            0x30, 0x82};

        // device 0x73 CID
        private readonly byte[] _response731A80 = {
            0x9F, 0xF1, 0x73, 0x5A, 0x80, 0x00, 0x00, 0x09, 0x15, 0x19,
            0x79, 0x17, 0x02, 0x0A, 0x30, 0x49, 0x41, 0x20, 0x07, 0x05,
            0x25, 0x17, 0x00, 0x0B, 0xF5, 0x06, 0x09, 0x00, 0x03, 0x03,
            0x00, 0x00, 0x00, 0x00};

        private readonly byte[] _response731802FFFF = {
            0x82, 0xF1, 0x73, 0x58, 0x00};

        // device 0x78 IHK2
        private readonly byte[] _response781A80 = {
            0x9F, 0xF1, 0x78, 0x5A, 0x80, 0x00, 0x00, 0x09, 0x24, 0x87,
            0x02, 0x15, 0x0D, 0x07, 0x92, 0x47, 0x4C, 0x20, 0x07, 0x05,
            0x25, 0x21, 0x00, 0x15, 0x06, 0x05, 0x3D, 0xFF, 0x03, 0x03,
            0x3C, 0x00, 0x00, 0x00};

        private readonly byte[] _response781802FFFF = {
            0x82, 0xF1, 0x78, 0x58, 0x00};

        // device 0x64 PDC
        private readonly byte[] _response641A80 = {
            0x9F, 0xF1, 0x64, 0x5A, 0x80, 0x00, 0x00, 0x09, 0x18, 0x51,
            0x39, 0x01, 0x02, 0x04, 0x21, 0x4C, 0x57, 0x20, 0x07, 0x05,
            0x31, 0x11, 0x00, 0x0C, 0x5F, 0x09, 0x0F, 0x00, 0x03, 0x03,
            0x1E, 0x00, 0x00, 0x00, 0x59};

        private readonly byte[] _response641802FFFF = {
            0x85, 0xF1, 0x64, 0x58, 0x01, 0xE2, 0x05, 0x24};

        private readonly byte[] _response6417E205 = {
            0x8C, 0xF1, 0x64, 0x57, 0x01, 0xE2, 0x05, 0x24, 0x01, 0x44,
            0x4A, 0x7B, 0x00, 0x00, 0x00};

        // device 0x65 SZM
        private readonly byte[] _response651A80 = {
            0x9F, 0xF1, 0x65, 0x5A, 0x80, 0x00, 0x00, 0x09, 0x18, 0x32,
            0x33, 0x03, 0x04, 0x07, 0x18, 0x44, 0x55, 0x20, 0x06, 0x08,
            0x15, 0x05, 0x00, 0x15, 0x1A, 0x02, 0x05, 0x01, 0x03, 0x03,
            0x3C, 0x00, 0x00, 0x00};

        private readonly byte[] _response651802FFFF = {
            0x85, 0xF1, 0x65, 0x58, 0x01, 0x9F, 0xF1, 0x24};

        private readonly byte[] _response65179FF1 = {
            0x89, 0xF1, 0x65, 0x57, 0x01, 0x9F, 0xF1, 0x24, 0x00, 0x00,
            0x44, 0x0E};

        private readonly byte[] _response6521F907 = {
            0x85, 0xF1, 0x65, 0x61, 0xF9, 0x07, 0x00, 0x00};

        // device 0x78 IHK
        private readonly byte[] _response78300201 = {
            0x9F, 0xF1, 0x78, 0x70, 0x02, 0x01, 0xC3, 0x28, 0x50, 0x64,
            0x69, 0x65, 0x3F, 0xFF, 0xFF, 0x0E, 0x10, 0x0E, 0x10, 0x59,
            0x53, 0x00, 0xAA, 0xAA, 0xC8, 0x00, 0x00, 0x00, 0xFF, 0xFB,
            0x00, 0x00, 0x00, 0x0F};

        private readonly byte[] _response78300601 = {
            0x86, 0xF1, 0x78, 0x70, 0x06, 0x01, 0x00, 0x00, 0x00};
        // ReSharper restore InconsistentNaming

        public bool Moving
        {
            get;
            set;
        }

        public bool VariableValues
        {
            get;
            set;
        }

        public bool IgnitionOk
        {
            get;
            set;
        }

        public bool ErrorDefault
        {
            get;
            set;
        }

        public CommThread()
        {
            _stopThread = false;
            _threadRunning = false;
            _workerThread = null;
            _pcanHandle = PCANBasic.PCAN_NONEBUS;
            _lastCanSendTick = DateTime.MinValue.Ticks;
            _tcpServerDiag = null;
            _tcpClientDiag = null;
            _tcpClientDiagStream = null;
            _tcpServerControl = null;
            _tcpClientControl = null;
            _tcpClientControlStream = null;
            _udpClient = null;
            _udpSocket = null;
            _udpBuffer = new byte[0x100];
            _udpError = false;
            _lastTcpDiagRecTick = DateTime.MinValue.Ticks;
            _serialPort = new SerialPort();
            _serialPort.DataReceived += SerialDataReceived;
            _serialReceiveEvent = new AutoResetEvent(false);
            _pcanReceiveEvent = new AutoResetEvent(false);
            _sendData = new byte[260];
            _receiveData = new byte[260];
            _receiveDataMotorBackup = new byte[_receiveData.Length];
            _noResponseCount = 0;
            for (int i = 0; i < _timeValveWrite.Length; i++)
            {
                _timeValveWrite[i] = new Stopwatch();
            }
            _mode = 0x00;
            _outputs = 0x00;
            _axisPosPrescaler = 0;
            _axisPosRaw = 0;
            _axisPosFilt = _axisPosRaw;
            _batteryVoltage = 1445;
            _speed = 0;
            _compressorRunningTime = 0;
            _idleSpeedControl = 0;
            _ecuErrorResetList = new List<byte>();
            _timeIdleSpeedControlWrite = new Stopwatch();
            _receiveStopWatch = new Stopwatch();
            Moving = false;
            VariableValues = false;
            IgnitionOk = false;
        }

        public bool StartThread(string comPort, ConceptType conceptType, bool adsAdapter, bool klineResponder, ResponseType responseType, ConfigData configData)
        {
            try
            {
                StopThread();
                _stopThread = false;
                _comPort = comPort;
                _conceptType = conceptType;
                _adsAdapter = adsAdapter;
                _klineResponder = klineResponder;
                _responseType = responseType;
                _configData = configData;
                foreach (ResponseEntry responseEntry in _configData.ResponseList)
                {
                    responseEntry.Reset();
                }
                _workerThread = new Thread(ThreadFunc);
                _threadRunning = true;
                _workerThread.Priority = ThreadPriority.Highest;
                _workerThread.Start();
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        public void StopThread()
        {
            if (_workerThread != null)
            {
                _stopThread = true;
                _workerThread.Join();
                _workerThread = null;
            }
        }

        public bool ThreadRunning()
        {
            if (_workerThread == null) return false;
            return _threadRunning;
        }

        private void ThreadFunc()
        {
            if (Connect())
            {
                foreach (Stopwatch watch in _timeValveWrite)
                {
                    watch.Stop();
                }
                _outputs = 0x00;
                _noResponseCount = 0;
                _ecuErrorResetList.Clear();
                ErrorDefault = false;
                while (!_stopThread)
                {
                    if (ErrorDefault)
                    {
                        ErrorDefault = false;
                        _ecuErrorResetList.Clear();
                    }
                    try
                    {
                        switch (_conceptType)
                        {
                            case ConceptType.Concept1:
                                SerialConcept1Transmission();
                                break;

                            case ConceptType.ConceptIso9141:
                                SerialIso9141Transmission();
                                break;

                            case ConceptType.Concept3:
                                SerialConcept3Transmission();
                                break;

                            default:
                                SerialTransmission();
                                break;
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
                Disconnect();
            }
            _threadRunning = false;
        }

        private bool Connect()
        {
            Disconnect();
            if (_comPort.StartsWith("ENET", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    switch (_conceptType)
                    {
                        case ConceptType.ConceptBwmFast:
                            break;

                        default:
                            return false;
                    }
                    _tcpServerDiag = new TcpListener(IPAddress.Any, EnetDiagPort);
                    _tcpServerDiag.Start();

                    _tcpServerControl = new TcpListener(IPAddress.Any, EnetControlPort);
                    _tcpServerControl.Start();

                    UdpConnect();
                }
                catch (Exception)
                {
                    Disconnect();
                    return false;
                }

                return true;
            }
            if (_comPort.StartsWith("CAN", StringComparison.OrdinalIgnoreCase))
            {
                TPCANBaudrate baudRate;
                switch (_conceptType)
                {
                    case ConceptType.ConceptBwmFast:
                        baudRate = TPCANBaudrate.PCAN_BAUD_500K;
                        //baudRate = TPCANBaudrate.PCAN_BAUD_100K;
                        break;

                    default:
                        return false;
                }
                TPCANStatus stsResult = PCANBasic.Initialize(PCANBasic.PCAN_USBBUS1, baudRate, TPCANType.PCAN_TYPE_DNG, 0, 0);
                if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                {
                    return false;
                }
                _pcanHandle = PCANBasic.PCAN_USBBUS1;
                UInt32 iEventBuffer = Convert.ToUInt32(_pcanReceiveEvent.SafeWaitHandle.DangerousGetHandle().ToInt32());
                stsResult = PCANBasic.SetValue(_pcanHandle, TPCANParameter.PCAN_RECEIVE_EVENT, ref iEventBuffer, sizeof(UInt32));
                if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                {
                    Disconnect();
                    return false;
                }
                return true;
            }
            try
            {
                int baudRate = 9600;
                Parity parity = Parity.Even;
                switch (_conceptType)
                {
                    case ConceptType.ConceptBwmFast:
                        baudRate = 115200;
                        parity = Parity.None;
                        break;

                    case ConceptType.ConceptKwp2000Bmw:
                        baudRate = 10400;
                        parity = Parity.None;
                        break;

                    case ConceptType.ConceptKwp2000S:
                    case ConceptType.ConceptDs2:
                    case ConceptType.Concept1:
                        baudRate = 9600;
                        parity = Parity.Even;
                        break;

                    case ConceptType.ConceptIso9141:
                        baudRate = 10400;
                        //baudRate = 9600;
                        parity = Parity.None;
                        break;

                    case ConceptType.Concept3:
                        baudRate = 9600;
                        parity = Parity.Even;
                        break;
                }

                _serialPort.PortName = _comPort;
                _serialPort.BaudRate = baudRate;
                _serialPort.DataBits = 8;
                _serialPort.Parity = parity;
                _serialPort.StopBits = StopBits.One;
                _serialPort.Handshake = Handshake.None;
                _serialPort.ReadTimeout = 0;
                _serialPort.DtrEnable = false;
                _serialPort.RtsEnable = false;
                _serialPort.Open();
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        private void Disconnect()
        {
            UdpDisconnect();

            // diag port
            EnetDiagClose();

            try
            {
                if (_tcpServerDiag != null)
                {
                    _tcpServerDiag.Stop();
                    _tcpServerDiag = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            // control port
            EnetControlClose();

            try
            {
                if (_tcpServerControl != null)
                {
                    _tcpServerControl.Stop();
                    _tcpServerControl = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }
            if (_pcanHandle != PCANBasic.PCAN_NONEBUS)
            {
                PCANBasic.Uninitialize(_pcanHandle);
                _pcanHandle = PCANBasic.PCAN_NONEBUS;
            }
            try
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void UdpConnect()
        {
            // a virtual network adapter with an auto ip address
            // is required tp receive the UPD broadcasts
            _udpError = false;
#if USE_UDP_SOCKET
            _udpSocket =new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPEndPoint ipUdp = new IPEndPoint(IPAddress.Any, enetControlPort);
            _udpSocket.Bind(ipUdp);
            StartUdpSocketListen();
#else
            _udpClient = new UdpClient(EnetControlPort);
            StartUdpListen();
#endif
        }

        private void UdpDisconnect()
        {
            try
            {
                if (_udpSocket != null)
                {
                    _udpSocket.Close();
                    _udpSocket = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                if (_udpClient != null)
                {
                    _udpClient.Close();
                    _udpClient = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private void UdpRecover()
        {
            try
            {
                if (_udpError)
                {
                    UdpDisconnect();
                    UdpConnect();
                }
            }
            catch (Exception)
            {
                _udpError = true;
            }
        }

        private void UpdateOutState()
        {
            try
            {
                if (_serialPort.DtrEnable != IgnitionOk)
                {
                    _serialPort.DtrEnable = IgnitionOk;
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private bool ReceiveWakeUp(out byte address)
        {
            address = 0;
            try
            {
                while (!_stopThread)
                {
                    UpdateOutState();
                    if (_klineResponder)
                    {
                        if (_serialPort.CtsHolding)
                        {   // start bit
                            break;
                        }
                    }
                    else
                    {
                        if (_serialPort.DsrHolding)
                        {   // start bit
                            break;
                        }
                    }
                    Thread.Sleep(10);
                }
                if (_stopThread) return false;
                Thread.Sleep(100);
                int recValue = 0x00;
                for (int i = 0; i < 9; i++)
                {
                    Thread.Sleep(200);
                    if (_klineResponder)
                    {
                        if (!_serialPort.CtsHolding)
                        {
                            recValue |= (1 << i);
                        }
                    }
                    else
                    {
                        if (!_serialPort.DsrHolding)
                        {
                            recValue |= (1 << i);
                        }
                    }
                    if (_stopThread) return false;
                }
                if ((recValue & 0x100) == 0)
                {   // invalid stop bit
                    return false;
                }
                Thread.Sleep(100);
                address = (byte)recValue;
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            _serialReceiveEvent.Set();
        }

        private bool SendData(byte[] sendData, int length)
        {   // no try catch to allow loop exit
            //_serialPort.DiscardInBuffer();
            _serialPort.Write(sendData, 0, length);
            if (!RemoveEcho(sendData, 0, length))
            {
                return false;
            }
            return true;
        }

        private bool SendData(byte[] sendData, int offset, int length)
        {   // no try catch to allow loop exit
            _serialPort.DiscardInBuffer();
            _serialPort.Write(sendData, offset, length);
            if (!RemoveEcho(sendData, offset, length))
            {
                return false;
            }
            return true;
        }

        private bool RemoveEcho(byte[] sendData, int offset, int length)
        {
            if (_klineResponder)
            {   // remove echo
                byte[] buffer = new byte[length];
                if (!ReceiveData(buffer, 0, length))
                {
                    return false;
                }
                for (int i = 0; i < length; i++)
                {
                    if (buffer[i] != sendData[i + offset])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private bool ReceiveData(byte[] receiveData, int offset, int length)
        {
            try
            {
                UpdateOutState();
                // wait for first byte
                // for stable switching we always need 10ms, but then are problems with win CE client
                int interByteTimeout = _conceptType == ConceptType.ConceptBwmFast ? 30 : 10;
                int lastBytesToRead = 0;
                int recLen = 0;
                _receiveStopWatch.Reset();
                _receiveStopWatch.Start();
                for (; ; )
                {
                    int bytesToRead = _serialPort.BytesToRead;
                    if (bytesToRead >= length)
                    {
                        recLen = _serialPort.Read(receiveData, offset + recLen, length - recLen);
                    }
                    if (recLen >= length)
                    {
                        break;
                    }
                    if (lastBytesToRead != bytesToRead)
                    {   // bytes received
                        _receiveStopWatch.Reset();
                        _receiveStopWatch.Start();
                        lastBytesToRead = bytesToRead;
                    }
                    else
                    {
                        if (_receiveStopWatch.ElapsedMilliseconds > interByteTimeout)
                        {
                            break;
                        }
                    }
                    // no _serialReceiveEvent.WaitOne(1, false); allowed here!
                }
                _receiveStopWatch.Stop();
                if (recLen < length)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        private bool ReceiveData(byte[] receiveData, int offset, int length, int timeout, int timeoutTelEnd)
        {
            try
            {
                // wait for first byte
                int lastBytesToRead;
                _receiveStopWatch.Reset();
                _receiveStopWatch.Start();
                for (; ; )
                {
                    lastBytesToRead = _serialPort.BytesToRead;
                    if (lastBytesToRead > 0)
                    {
                        break;
                    }
                    if (_receiveStopWatch.ElapsedMilliseconds > timeout)
                    {
                        _receiveStopWatch.Stop();
                        return false;
                    }
                    _serialReceiveEvent.WaitOne(1, false);
                }

                int recLen = 0;
                _receiveStopWatch.Reset();
                _receiveStopWatch.Start();
                for (; ; )
                {
                    int bytesToRead = _serialPort.BytesToRead;
                    if (bytesToRead >= length)
                    {
                        recLen += _serialPort.Read(receiveData, offset + recLen, length - recLen);
                    }
                    if (recLen >= length)
                    {
                        break;
                    }
                    if (lastBytesToRead != bytesToRead)
                    {   // bytes received
                        _receiveStopWatch.Reset();
                        _receiveStopWatch.Start();
                        lastBytesToRead = bytesToRead;
                    }
                    else
                    {
                        if (_receiveStopWatch.ElapsedMilliseconds > timeoutTelEnd)
                        {
                            break;
                        }
                    }
                    _serialReceiveEvent.WaitOne(1, false);
                }
                _receiveStopWatch.Stop();
                if (recLen < length)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool ObdSend(byte[] sendData)
        {
            switch (_conceptType)
            {
                case ConceptType.ConceptBwmFast:
                case ConceptType.ConceptKwp2000Bmw:
                    if (_tcpServerDiag != null)
                    {
                        return SendEnet(sendData);
                    }
                    if (_pcanHandle != PCANBasic.PCAN_NONEBUS)
                    {
                        return SendCan(sendData);
                    }
                    return SendBmwfast(sendData);

                case ConceptType.ConceptKwp2000S:
                    {
                        byte[] tempArray = new byte[260];
                        // convert to KWP2000*
                        int dataLength = sendData[0] & 0x3F;
                        if (dataLength == 0)
                        {   // with length byte
                            dataLength = sendData[3];
                            Array.Copy(sendData, 0, tempArray, 0, dataLength + 3);
                            tempArray[0] = 0xB8;
                        }
                        else
                        {   // without length byte
                            Array.Copy(sendData, 0, tempArray, 0, 3);
                            Array.Copy(sendData, 3, tempArray, 4, dataLength);
                            tempArray[0] = 0xB8;
                            tempArray[3] = (byte)dataLength;
                        }
                        return SendKwp2000S(tempArray);
                    }

                case ConceptType.ConceptDs2:
                    {
                        byte[] tempArray = new byte[260];
                        // convert to DS2
                        int dataLength = sendData[0] & 0x3F;
                        byte ecuAddr = sendData[2];
                        if (ecuAddr == 0xF1)
                        {   // for echo response
                            ecuAddr = sendData[1];
                        }
                        if (dataLength == 0)
                        {   // with length byte
                            dataLength = sendData[3];
                            Array.Copy(sendData, 4, tempArray, 2, dataLength);
                            tempArray[0] = ecuAddr;
                            tempArray[1] = (byte)(dataLength + 3);
                        }
                        else
                        {   // without length byte
                            Array.Copy(sendData, 3, tempArray, 2, dataLength);
                            tempArray[0] = ecuAddr;
                            tempArray[1] = (byte)(dataLength + 3);
                        }
                        return SendDs2(tempArray);
                    }
            }
            return false;
        }

        private bool ObdReceive(byte[] receiveData)
        {
            switch (_conceptType)
            {
                case ConceptType.ConceptBwmFast:
                case ConceptType.ConceptKwp2000Bmw:
                    if (_tcpServerDiag != null)
                    {
                        UdpRecover();
                        ReceiveEnetControl();
                        return ReceiveEnet(receiveData);
                    }
                    if (_pcanHandle != PCANBasic.PCAN_NONEBUS)
                    {
                        return ReceiveCan(receiveData);
                    }
                    return ReceiveBmwFast(receiveData);

                case ConceptType.ConceptKwp2000S:
                    {
                        if (!ReceiveKwp2000S(receiveData))
                        {
                            return false;
                        }
                        // convert to BMW-FAST
                        int dataLength = receiveData[3];
                        if (dataLength > 0x3F)
                        {   // with length byte
                            receiveData[0] = 0x80;
                            receiveData[dataLength + 4] = CalcChecksumBmwFast(receiveData, dataLength + 4);
                        }
                        else
                        {   // without length byte
                            byte[] tempArray = new byte[260];
                            Array.Copy(receiveData, 4, tempArray, 0, dataLength);
                            Array.Copy(tempArray, 0, receiveData, 3, dataLength);
                            receiveData[0] = (byte)(0x80 | dataLength);
                            receiveData[dataLength + 3] = CalcChecksumBmwFast(receiveData, dataLength + 3);
                        }
                        return true;
                    }

                case ConceptType.ConceptDs2:
                    {
                        if (!ReceiveDs2(receiveData))
                        {
                            return false;
                        }
                        // convert to BMW-FAST
                        int dataLength = receiveData[1] - 3;
                        if (dataLength > 0x3F)
                        {   // with length byte
                            byte[] tempArray = new byte[260];
                            Array.Copy(receiveData, 2, tempArray, 4, dataLength);
                            tempArray[0] = 0x80;
                            tempArray[1] = receiveData[0];
                            tempArray[2] = 0xF1;
                            tempArray[3] = (byte)dataLength;
                            Array.Copy(tempArray, 0, receiveData, 0, dataLength + 4);
                            receiveData[dataLength + 4] = CalcChecksumBmwFast(receiveData, dataLength + 4);
                        }
                        else
                        {   // without length byte
                            byte[] tempArray = new byte[260];
                            Array.Copy(receiveData, 2, tempArray, 3, dataLength);
                            tempArray[0] = (byte)(dataLength | 0x80);
                            tempArray[1] = receiveData[0];
                            tempArray[2] = 0xF1;
                            Array.Copy(tempArray, 0, receiveData, 0, dataLength + 3);
                            receiveData[dataLength + 3] = CalcChecksumBmwFast(receiveData, dataLength + 3);
                        }
                        return true;
                    }
            }
            return false;
        }

        private void StartUdpListen()
        {
            _udpClient.BeginReceive(UdpReceiver, new Object());
        }

        private void UdpReceiver(IAsyncResult ar)
        {
            try
            {
                UdpClient udpClientLocal = _udpClient;
                if (udpClientLocal == null)
                {
                    return;
                }
                IPEndPoint ip = new IPEndPoint(IPAddress.Any, 0);
                byte[] bytes = udpClientLocal.EndReceive(ar, ref ip);
#if false
                if (bytes != null)
                {
                    string text = string.Empty;
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        text += string.Format("{0:X02} ", bytes[i]);
                    }
                    Debug.WriteLine("Udp: " + text);
                }
#endif
                if (bytes != null && bytes.Length == 6 && bytes[5] == 0x11)
                {
                    byte[] identMessage = new byte[6 + 50];
                    int idx = 0;
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = (byte)(identMessage.Length - 6);
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = 0x04;     // Anouncement
                    // TESTER ID
                    identMessage[idx++] = (byte)'D';
                    identMessage[idx++] = (byte)'I';
                    identMessage[idx++] = (byte)'A';
                    identMessage[idx++] = (byte)'G';
                    identMessage[idx++] = (byte)'A';
                    identMessage[idx++] = (byte)'D';
                    identMessage[idx++] = (byte)'R';
                    identMessage[idx++] = (byte)'1';
                    identMessage[idx++] = (byte)'0';
                    // MAC
                    identMessage[idx++] = (byte)'B';
                    identMessage[idx++] = (byte)'M';
                    identMessage[idx++] = (byte)'W';
                    identMessage[idx++] = (byte)'M';
                    identMessage[idx++] = (byte)'A';
                    identMessage[idx++] = (byte)'C';
                    for (int i = 0; i < 12; i++)
                    {
                        identMessage[idx++] = (byte)('0'+ (i % 10));
                    }
                    // VIN
                    identMessage[idx++] = (byte)'B';
                    identMessage[idx++] = (byte)'M';
                    identMessage[idx++] = (byte)'W';
                    identMessage[idx++] = (byte)'V';
                    identMessage[idx++] = (byte)'I';
                    identMessage[idx++] = (byte)'N';
                    for (int i = 0; i < 17; i++)
                    {
                        identMessage[idx++] = (byte)('a' + i);
                    }
                    _udpClient.Send(identMessage, identMessage.Length, ip);
                }
                StartUdpListen();
            }
            catch (Exception)
            {
                _udpError = true;
            }
        }

        private void StartUdpSocketListen()
        {
            IPEndPoint ip = new IPEndPoint(IPAddress.Any, 0);
            EndPoint tempRemoteEp = ip;
            _udpSocket.BeginReceiveFrom(_udpBuffer, 0, _udpBuffer.Length, SocketFlags.None, ref tempRemoteEp, UdpSocketReceiver, _udpSocket);
        }

        private void UdpSocketReceiver(IAsyncResult ar)
        {
            try
            {
                Socket udpSocketLocal = _udpSocket;
                if (udpSocketLocal == null)
                {
                    return;
                }
                IPEndPoint ip = new IPEndPoint(IPAddress.Any, 0);
                EndPoint tempRemoteEp = ip;
                int recLen = udpSocketLocal.EndReceiveFrom(ar, ref tempRemoteEp);
#if false
                if (recLen > 0)
                {
                    string text = string.Empty;
                    for (int i = 0; i < recLen; i++)
                    {
                        text += string.Format("{0:X02} ", _udpBuffer[i]);
                    }
                    Debug.WriteLine("Udp: " + text);
                }
#endif
                if (recLen == 6 && _udpBuffer[5] == 0x11)
                {
                    byte[] identMessage = new byte[6 + 50];
                    int idx = 0;
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = (byte)(identMessage.Length - 6);
                    identMessage[idx++] = 0x00;
                    identMessage[idx++] = 0x04;     // Anouncement
                    // TESTER ID
                    identMessage[idx++] = (byte)'D';
                    identMessage[idx++] = (byte)'I';
                    identMessage[idx++] = (byte)'A';
                    identMessage[idx++] = (byte)'G';
                    identMessage[idx++] = (byte)'A';
                    identMessage[idx++] = (byte)'D';
                    identMessage[idx++] = (byte)'R';
                    identMessage[idx++] = (byte)'1';
                    identMessage[idx++] = (byte)'0';
                    // MAC
                    for (int i = 0; i < 18; i++)
                    {
                        identMessage[idx++] = (byte)('0' + (i % 10));
                    }
                    // VIN
                    for (int i = 0; i < 23; i++)
                    {
                        identMessage[idx++] = (byte)('a' + i);
                    }
                    udpSocketLocal.SendTo(identMessage, tempRemoteEp);
                }
                StartUdpSocketListen();
            }
            catch (Exception)
            {
                _udpError = true;
            }
        }

        private void EnetControlClose()
        {
            try
            {
                if (_tcpClientControlStream != null)
                {
                    _tcpClientControlStream.Close();
                    _tcpClientControlStream = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                if (_tcpClientControl != null)
                {
                    Debug.WriteLine("Control Closed");
                    _tcpClientControl.Close();
                    _tcpClientControl = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private bool ReceiveEnetControl()
        {
            try
            {
                if (!IsTcpClientConnected(_tcpClientControl))
                {
                    EnetControlClose();
                    if (!_tcpServerControl.Pending())
                    {
                        Thread.Sleep(10);
                        return false;
                    }
                    _tcpClientControl = _tcpServerControl.AcceptTcpClient();
                    _tcpClientControlStream = _tcpClientControl.GetStream();
                }
            }
            catch (Exception)
            {
                EnetControlClose();
            }

            try
            {
                if (_tcpClientControlStream != null && _tcpClientControlStream.DataAvailable)
                {
                    byte[] dataBuffer = new byte[0x200];
                    int recLen = _tcpClientControlStream.Read(dataBuffer, 0, dataBuffer.Length);
#if false
                    string text = string.Empty;
                    for (int i = 0; i < recLen; i++)
                    {
                        text += string.Format("{0:X02} ", dataBuffer[i]);
                    }
                    Debug.WriteLine("Ctrl Rec: " + text);
#endif
                    if (recLen >= 6 && dataBuffer[5] == 0x10)
                    {   // ignition state
                        // send response
                        byte[] responseBuffer = new byte[6 + 1];
                        responseBuffer[2] = (byte)((responseBuffer.Length - 6) >> 8);
                        responseBuffer[3] = (byte)(responseBuffer.Length - 6);
                        responseBuffer[4] = 0x00;
                        responseBuffer[5] = 0x10;   // ignition state
                        responseBuffer[6] = (byte)(IgnitionOk ? 0x05 : 0x00);   // Clamp state, Bit3,4 = 1 -> ignition on
                        _tcpClientControlStream.Write(responseBuffer, 0, responseBuffer.Length);
                    }

                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        private void EnetDiagClose()
        {
            try
            {
                if (_tcpClientDiagStream != null)
                {
                    _tcpClientDiagStream.Close();
                    _tcpClientDiagStream = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                if (_tcpClientDiag != null)
                {
                    Debug.WriteLine("Diag Closed");
                    _tcpClientDiag.Close();
                    _tcpClientDiag = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private bool ReceiveEnet(byte[] receiveData)
        {
            try
            {
                if (!IsTcpClientConnected(_tcpClientDiag))
                {
                    EnetDiagClose();
                    if (!_tcpServerDiag.Pending())
                    {
                        Thread.Sleep(10);
                        return false;
                    }
                    _tcpClientDiag = _tcpServerDiag.AcceptTcpClient();
                    _tcpClientDiagStream = _tcpClientDiag.GetStream();
                    _lastTcpDiagRecTick = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception)
            {
                EnetDiagClose();
            }

            try
            {
                if (_tcpClientDiagStream != null)
                {
                    if ((Stopwatch.GetTimestamp() - _lastTcpDiagRecTick) > 2000 * TickResolMs)
                    {
                        _lastTcpDiagRecTick = Stopwatch.GetTimestamp();
                        byte[] dataBuffer = new byte[6 + 2];
                        dataBuffer[0] = 0x00;
                        dataBuffer[1] = 0x00;
                        dataBuffer[2] = 0x00;
                        dataBuffer[3] = 0x02;
                        dataBuffer[4] = 0x00;
                        dataBuffer[5] = 0x12;   // Payoad type: alive check
                        dataBuffer[6] = 0xF4;
                        dataBuffer[7] = 0x00;
                        _tcpClientDiagStream.Write(dataBuffer, 0, dataBuffer.Length);
                        Debug.WriteLine("Alive Check");
                    }
                }
            }
            catch (Exception)
            {
                // ignored
            }

            try
            {
                if (_tcpClientDiagStream != null && _tcpClientDiagStream.DataAvailable)
                {
                    _lastTcpDiagRecTick = Stopwatch.GetTimestamp();
                    byte[] dataBuffer = new byte[0x200];
                    int recLen = _tcpClientDiagStream.Read(dataBuffer, 0, 6);
                    if (recLen < 6)
                    {
                        return false;
                    }
                    int payloadLength = (((int)dataBuffer[0] << 24) | ((int)dataBuffer[1] << 16) | ((int)dataBuffer[2] << 8) | dataBuffer[3]);
                    if (payloadLength > dataBuffer.Length - 6)
                    {
                        while (_tcpClientDiagStream.DataAvailable)
                        {
                            _tcpClientDiagStream.ReadByte();
                        }
                        return false;
                    }
                    if (payloadLength > 0)
                    {
                        recLen += _tcpClientDiagStream.Read(dataBuffer, 6, payloadLength);
                    }
                    if (recLen < payloadLength + 6)
                    {
                        return false;
                    }
#if false
                    string text = string.Empty;
                    for (int i = 0; i < recLen; i++)
                    {
                        text += string.Format("{0:X02} ", dataBuffer[i]);
                    }
                    Debug.WriteLine("Rec: " + text);
#endif
                    int dataLen = payloadLength - 2;
                    if ((dataLen < 1) || ((dataLen + 8) > recLen))
                    {
                        return false;
                    }
                    int payloadType = dataBuffer[5];
                    if (payloadType != 0x01)
                    {
                        return false;
                    }
                    // send ack
                    byte[] ack = new byte[recLen];
                    Array.Copy(dataBuffer, ack, ack.Length);
                    ack[5] = 0x02;

                    if (recLen == 14 && ack[8] == 0x19)
                    {
                        Debug.WriteLine("FS_LESEN_DETAIL Ack");
                        int ackLength = payloadLength - 1;
                        ack[0] = (byte)((ackLength >> 24) & 0xFF);
                        ack[1] = (byte)((ackLength >> 16) & 0xFF);
                        ack[2] = (byte)((ackLength >> 8) & 0xFF);
                        ack[3] = (byte)(ackLength & 0xFF);
                        _tcpClientDiagStream.Write(ack, 0, ackLength + 8 - 2);
                    }
                    else
                    {
                        _tcpClientDiagStream.Write(ack, 0, ack.Length);
                    }

                    // create BMW-FAST telegram
                    byte sourceAddr = dataBuffer[6];
                    byte targetAddr = dataBuffer[7];
                    int len;
                    if (sourceAddr == TcpTesterAddr) sourceAddr = 0xF1;
                    if (dataLen > 0x3F)
                    {
                        receiveData[0] = 0x80;
                        receiveData[1] = targetAddr;
                        receiveData[2] = sourceAddr;
                        receiveData[3] = (byte)dataLen;
                        Array.Copy(dataBuffer, 8, receiveData, 4, dataLen);
                        len = dataLen + 4;
                    }
                    else
                    {
                        receiveData[0] = (byte)(0x80 | dataLen);
                        receiveData[1] = targetAddr;
                        receiveData[2] = sourceAddr;
                        Array.Copy(dataBuffer, 8, receiveData, 3, dataLen);
                        len = dataLen + 3;
                    }
                    if ((targetAddr == 0xED) || (targetAddr == 0xEF) || (targetAddr == 0xDF))
                    {   // functional address
                        receiveData[0] |= 0xC0;
                    }
                    receiveData[len] = CalcChecksumBmwFast(receiveData, len);
                    return true;
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            catch (Exception)
            {
                Thread.Sleep(10);
                return false;
            }
            return false;
        }

        private bool SendEnet(byte[] sendData)
        {
            if (_tcpClientDiagStream == null)
            {
                return false;
            }
            try
            {
                byte targetAddr = sendData[1];
                byte sourceAddr = sendData[2];
                if (targetAddr == 0xF1) targetAddr = TcpTesterAddr;
                int dataOffset = 3;
                int dataLength = sendData[0] & 0x3F;
                if (dataLength == 0)
                {   // with length byte
                    if (sendData[3] == 0)
                    {
                        dataLength = (sendData[4] << 8) | sendData[5];
                        dataOffset = 6;
                    }
                    else
                    {
                        dataLength = sendData[3];
                        dataOffset = 4;
                    }
                }
                byte[] dataBuffer = new byte[dataLength + 8];
                int payloadLength = dataLength + 2;
                dataBuffer[0] = (byte)((payloadLength >> 24) & 0xFF);
                dataBuffer[1] = (byte)((payloadLength >> 16) & 0xFF);
                dataBuffer[2] = (byte)((payloadLength >> 8) & 0xFF);
                dataBuffer[3] = (byte)(payloadLength & 0xFF);
                dataBuffer[4] = 0x00;
                dataBuffer[5] = 0x01;   // Payoad type: Diag message
                dataBuffer[6] = sourceAddr;
                dataBuffer[7] = targetAddr;
                Array.Copy(sendData, dataOffset, dataBuffer, 8, dataLength);
#if false
                string text = string.Empty;
                for (int i = 0; i < dataBuffer.Length; i++)
                {
                    text += string.Format("{0:X02} ", dataBuffer[i]);
                }
                Debug.WriteLine("Send: " + text);
#endif
                _tcpClientDiagStream.Write(dataBuffer, 0, dataBuffer.Length);
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        public bool IsTcpClientConnected(TcpClient tcpClient)
        {
            try
            {
                if (tcpClient?.Client != null && tcpClient.Client.Connected)
                {
                    // Detect if client disconnected
                    if (tcpClient.Client.Poll(0, SelectMode.SelectRead))
                    {
                        byte[] buff = new byte[1];
                        if (tcpClient.Client.Receive(buff, SocketFlags.Peek) == 0)
                        {
                            // Client disconnected
                            return false;
                        }
                        else
                        {
                            return true;
                        }
                    }

                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private bool ReceiveCan(byte[] receiveData)
        {
#if CAN_DEBUG
            long lastReceiveTime = Stopwatch.GetTimestamp();
#endif
            const byte blocksize = 0;
            const byte sepTime = 0;
            const byte waitCount = 0;
            byte fcCount = 0;
            int len;
            byte blockCount = 0;
            byte sourceAddr = 0;
            byte targetAddr = 0;
            byte[] dataBuffer = null;

            int recLen = 0;
            _receiveStopWatch.Reset();
            _receiveStopWatch.Start();
            for (; ; )
            {
                for (; ; )
                {
                    TPCANMsg canMsg;
                    TPCANTimestamp canTimeStamp;
                    TPCANStatus stsResult = PCANBasic.Read(_pcanHandle, out canMsg, out canTimeStamp);
                    if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                    {
                        break;
                    }
                    if ((canMsg.LEN < 2) || (canMsg.MSGTYPE != TPCANMessageType.PCAN_MESSAGE_STANDARD) ||
                        ((canMsg.ID & 0xFF00) != 0x0600))
                    {
                        continue;
                    }
                    byte frameType = (byte)((canMsg.DATA[1] >> 4) & 0x0F);
#if CAN_DEBUG
                    long receiveTime = Stopwatch.GetTimestamp();
                    long timeDiff = (receiveTime - lastReceiveTime) / TickResolMs;
                    lastReceiveTime = receiveTime;
                    Debug.WriteLine("Rec({0}): {1}", frameType, timeDiff);
#endif
                    if (recLen == 0)
                    {   // first telegram
                        sourceAddr = (byte)(canMsg.ID & 0xFF);
                        targetAddr = canMsg.DATA[0];
                        switch (frameType)
                        {
                            case 0: // single frame
                                len = canMsg.DATA[1] & 0x0F;
                                if (len > canMsg.LEN - 2)
                                {
                                    continue;
                                }
                                dataBuffer = new byte[len];
                                Array.Copy(canMsg.DATA, 2, dataBuffer, 0, len);
                                recLen = len;
                                _receiveStopWatch.Reset();
                                _receiveStopWatch.Start();
                                break;

                            case 1: // first frame
                                if (canMsg.LEN < 8)
                                {
                                    continue;
                                }
                                len = (((int)canMsg.DATA[1] & 0x0F) << 8) + canMsg.DATA[2];
                                dataBuffer = new byte[len];
                                Array.Copy(canMsg.DATA, 3, dataBuffer, 0, 5);
                                recLen = 5;
                                blockCount = 1;
                                {
                                    TPCANMsg sendMsg = new TPCANMsg
                                    {
                                        DATA = new byte[8],
                                        ID = (uint) (0x600 + targetAddr),
#if CAN_DYN_LEN
                                        LEN = 4,
#else
                                        LEN = 8,
#endif
                                        MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD
                                    };
                                    sendMsg.DATA[0] = sourceAddr;
                                    sendMsg.DATA[1] = 0x30;  // FC
                                    sendMsg.DATA[2] = blocksize;    // Block size
                                    sendMsg.DATA[3] = sepTime;      // Min sep. Time
                                    fcCount = blocksize;
                                    stsResult = PCANBasic.Write(_pcanHandle, ref sendMsg);
                                    if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                                    {
                                        _receiveStopWatch.Stop();
                                        return false;
                                    }
                                }
                                _receiveStopWatch.Reset();
                                _receiveStopWatch.Start();
                                break;

                            default:
                                continue;
                        }
                    }
                    else
                    {
                        if (frameType == 1)
                        {
                            continue;
                        }
                        if (frameType != 2)
                        {   // consecutive frame
                            continue;
                        }
                        if ((sourceAddr != (canMsg.ID & 0xFF)) || (targetAddr != canMsg.DATA[0]))
                        {
                            continue;
                        }
                        if ((canMsg.DATA[1] & 0x0F) != (blockCount & 0x0F))
                        {
                            continue;
                        }
                        if (dataBuffer == null)
                        {
                            return false;
                        }
                        len = dataBuffer.Length - recLen;
                        if (len > 6)
                        {
                            len = 6;
                        }
                        if (len > canMsg.LEN - 2)
                        {
                            continue;
                        }
                        Array.Copy(canMsg.DATA, 2, dataBuffer, recLen, len);
                        recLen += len;
                        blockCount++;
                        _receiveStopWatch.Reset();
                        _receiveStopWatch.Start();

                        if (fcCount > 0 && recLen < dataBuffer.Length)
                        {
                            fcCount--;
                            if (fcCount == 0)
                            {
#if CAN_DEBUG
                                Debug.WriteLine("Send FC");
#endif
                                TPCANMsg sendMsg = new TPCANMsg
                                {
                                    DATA = new byte[8],
                                    ID = (uint) (0x600 + targetAddr),
#if CAN_DYN_LEN
                                    LEN = 4,
#else
                                    LEN = 8,
#endif
                                    MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD
                                };

                                // sleep test
                                for (int i = 0; i < waitCount; i++)
                                {
                                    Thread.Sleep(500);
#if CAN_DEBUG
                                    Debug.WriteLine("Send FC wait");
#endif
                                    sendMsg.DATA[0] = sourceAddr;
                                    sendMsg.DATA[1] = 0x31;         // FC, wait
                                    sendMsg.DATA[2] = blocksize;    // Block size
                                    sendMsg.DATA[3] = sepTime;      // Min sep. Time
                                    stsResult = PCANBasic.Write(_pcanHandle, ref sendMsg);
                                    if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                                    {
                                        _receiveStopWatch.Stop();
                                        return false;
                                    }
                                }
                                // ReSharper disable ConditionIsAlwaysTrueOrFalse
                                // ReSharper disable HeuristicUnreachableCode
#pragma warning disable 162
                                if (waitCount > 0)
                                {
                                    Thread.Sleep(500);
                                }
#pragma warning restore 162
                                // ReSharper restore HeuristicUnreachableCode
                                // ReSharper restore ConditionIsAlwaysTrueOrFalse
                                _receiveStopWatch.Reset();
                                _receiveStopWatch.Start();

                                sendMsg.DATA[0] = sourceAddr;
                                sendMsg.DATA[1] = 0x30;         // FC
                                sendMsg.DATA[2] = blocksize;    // Block size
                                sendMsg.DATA[3] = sepTime;      // Min sep. Time
                                fcCount = blocksize;
                                stsResult = PCANBasic.Write(_pcanHandle, ref sendMsg);
                                if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                                {
                                    _receiveStopWatch.Stop();
                                    return false;
                                }
                            }
                        }
                    }
                    // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                    if (dataBuffer != null && recLen >= dataBuffer.Length)
                    {
                        break;
                    }
                }
                if (dataBuffer != null && recLen >= dataBuffer.Length)
                {
                    break;
                }
                if (_receiveStopWatch.ElapsedMilliseconds > 1000)
                {
#if CAN_DEBUG
                    Debug.WriteLine("Rec Timeout");
#endif
                    _receiveStopWatch.Stop();
                    return false;
                }
                _pcanReceiveEvent.WaitOne(10);
                if (recLen == 0)
                {   // nothing received
                    _receiveStopWatch.Stop();
                    return false;
                }
            }
            _receiveStopWatch.Stop();
#if CAN_DEBUG
            Debug.WriteLine("Rec OK");
#endif
            // create BMW-FAST telegram
            if (dataBuffer.Length > 0x3F)
            {
                receiveData[0] = 0x80;
                receiveData[1] = targetAddr;
                receiveData[2] = sourceAddr;
                receiveData[3] = (byte)dataBuffer.Length;
                Array.Copy(dataBuffer, 0, receiveData, 4, dataBuffer.Length);
                len = dataBuffer.Length + 4;
            }
            else
            {
                receiveData[0] = (byte)(0x80 | dataBuffer.Length);
                receiveData[1] = targetAddr;
                receiveData[2] = sourceAddr;
                Array.Copy(dataBuffer, 0, receiveData, 3, dataBuffer.Length);
                len = dataBuffer.Length + 3;
            }
            if ((targetAddr == 0xED) || (targetAddr == 0xEF) || (targetAddr == 0xDF))
            {   // functional address
                receiveData[0] |= 0xC0;
            }
            receiveData[len] = CalcChecksumBmwFast(receiveData, len);
            return true;
        }

        private bool SendCan(byte[] sendData)
        {
            TPCANMsg canMsg;
            TPCANTimestamp canTimeStamp;
            TPCANStatus stsResult;
            TPCANMsg sendMsg = new TPCANMsg
            {
                DATA = new byte[8]
            };
            byte blockSize = 0;
            byte sepTime = 0;

            byte targetAddr = sendData[1];
            byte sourceAddr = sendData[2];
            int dataOffset = 3;
            int dataLength = sendData[0] & 0x3F;
            if (dataLength == 0)
            {   // with length byte
                dataLength = sendData[3];
                dataOffset = 4;
            }

            if ((Stopwatch.GetTimestamp() - _lastCanSendTick) < 10 * TickResolMs)
            {
                Thread.Sleep(10);   // required for multiple telegrams
            }
            // clear input buffer
            while (PCANBasic.Read(_pcanHandle, out canMsg, out canTimeStamp) == TPCANStatus.PCAN_ERROR_OK)
            {
            }

            if (dataLength <= 6)
            {   // single frame
                sendMsg.ID = (uint)(0x600 + sourceAddr);
#if CAN_DYN_LEN
                sendMsg.LEN = (byte)(2 + dataLength);
#else
                sendMsg.LEN = 8;
#endif
                sendMsg.MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD;
                sendMsg.DATA[0] = targetAddr;
                sendMsg.DATA[1] = (byte)(0x00 | dataLength);  // SF
                Array.Copy(sendData, dataOffset, sendMsg.DATA, 2, dataLength);
                stsResult = PCANBasic.Write(_pcanHandle, ref sendMsg);
                if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                {
                    return false;
                }
                _lastCanSendTick = Stopwatch.GetTimestamp();
                return true;
            }
            // first frame
            sendMsg.ID = (uint)(0x600 + sourceAddr);
            sendMsg.LEN = 8;
            sendMsg.MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD;
            sendMsg.DATA[0] = targetAddr;
            sendMsg.DATA[1] = (byte)(0x10 | ((dataLength >> 8) & 0xFF));  // FF
            sendMsg.DATA[2] = (byte)dataLength;
            int len = 5;
            Array.Copy(sendData, dataOffset, sendMsg.DATA, 3, len);
            dataLength -= len;
            dataOffset += len;
            stsResult = PCANBasic.Write(_pcanHandle, ref sendMsg);
            if (stsResult != TPCANStatus.PCAN_ERROR_OK)
            {
                return false;
            }
            bool waitForFc = true;
            byte blockCount = 1;

            for (; ; )
            {
                if (waitForFc)
                {
                    bool wait;
                    do
                    {
                        _receiveStopWatch.Reset();
                        _receiveStopWatch.Start();
                        for (; ; )
                        {
                            stsResult = PCANBasic.Read(_pcanHandle, out canMsg, out canTimeStamp);
                            if (stsResult == TPCANStatus.PCAN_ERROR_OK)
                            {
                                if ((canMsg.LEN >= 4) && (canMsg.MSGTYPE == TPCANMessageType.PCAN_MESSAGE_STANDARD) &&
                                    ((canMsg.ID & 0xFF00) == 0x0600) &&
                                    ((canMsg.ID & 0xFF) == targetAddr) && (canMsg.DATA[0] == sourceAddr) &&
                                    ((canMsg.DATA[1] & 0xF0) == 0x30))
                                {
                                    break;
                                }
                            }
                            if (_receiveStopWatch.ElapsedMilliseconds > 1000)
                            {
                                _receiveStopWatch.Stop();
                                return false;
                            }
                        }
                        _receiveStopWatch.Stop();
                        switch (canMsg.DATA[1] & 0x0F)
                        {
                            case 0: // CTS
                                wait = false;
                                break;

                            case 1: // Wait
                                wait = true;
                                break;

                            default:
                                return false;
                        }
                        blockSize = canMsg.DATA[2];
                        sepTime = canMsg.DATA[3];
                    } while (wait);
#if CAN_DEBUG
                    Debug.WriteLine("FC: BS={0} ST={1}", blockSize, sepTime);
#endif
                }
#if false
                if (blockCount == 2)    // test telegram injection
                {
                    sendMsg.ID = (uint)(0x600 + sourceAddr + 1);
                    sendMsg.LEN = 8;
                    sendMsg.MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD;
                    sendMsg.DATA[0] = targetAddr;
                    sendMsg.DATA[1] = (byte)(0x00 | 6);  // SF
                    sendMsg.DATA[2] = 0xFF;
                    sendMsg.DATA[3] = 0xFF;
                    sendMsg.DATA[4] = 0xFF;
                    sendMsg.DATA[5] = 0xFF;
                    sendMsg.DATA[6] = 0xFF;
                    sendMsg.DATA[7] = 0xFF;
                    stsResult = PCANBasic.Write(_pcanHandle, ref sendMsg);
                    if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                    {
                        return false;
                    }
                }
#endif
#if false
                if (blockCount == 2) // test telegram injection
                {   // first frame
                    sendMsg.ID = (uint) (0x600 + sourceAddr + 1);
                    sendMsg.LEN = 8;
                    sendMsg.MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD;
                    sendMsg.DATA[0] = targetAddr;
                    sendMsg.DATA[1] = (byte) (0x10 | 0); // FF
                    sendMsg.DATA[2] = (byte) 10;    // length
                    sendMsg.DATA[3] = 0xFF;
                    sendMsg.DATA[4] = 0xFF;
                    sendMsg.DATA[5] = 0xFF;
                    sendMsg.DATA[6] = 0xFF;
                    sendMsg.DATA[7] = 0xFF;
                }
                if (blockCount == 3) // test!
                {   // consecutive frame
                    sendMsg.ID = (uint)(0x600 + sourceAddr + 1);
                    sendMsg.LEN = 8;
                    sendMsg.MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD;
                    sendMsg.DATA[0] = targetAddr;
                    sendMsg.DATA[1] = (byte)(0x20 | 1);  // CF, block count
                    sendMsg.DATA[2] = 0xFF;
                    sendMsg.DATA[3] = 0xFF;
                    sendMsg.DATA[4] = 0xFF;
                    sendMsg.DATA[5] = 0xFF;
                    sendMsg.DATA[6] = 0xFF;
                    sendMsg.DATA[7] = 0xFF;
                }
#endif
                // consecutive frame
                len = dataLength;
                if (len > 6)
                {
                    len = 6;
                }
                sendMsg.ID = (uint)(0x600 + sourceAddr);
#if CAN_DYN_LEN
                sendMsg.LEN = (byte)(2 + len);
#else
                sendMsg.LEN = 8;
#endif
                sendMsg.MSGTYPE = TPCANMessageType.PCAN_MESSAGE_STANDARD;
                sendMsg.DATA[0] = targetAddr;
                sendMsg.DATA[1] = (byte)(0x20 | (blockCount & 0x0F));  // CF
                Array.Copy(sendData, dataOffset, sendMsg.DATA, 2, len);
                dataLength -= len;
                dataOffset += len;
                blockCount++;
                //Thread.Sleep(900);    // timeout test
                stsResult = PCANBasic.Write(_pcanHandle, ref sendMsg);
                if (stsResult != TPCANStatus.PCAN_ERROR_OK)
                {
                    return false;
                }
                if (dataLength <= 0)
                {
                    break;
                }

                waitForFc = false;
                if (blockSize > 0)
                {
                    if (blockSize == 1)
                    {
                        waitForFc = true;
                    }
                    blockSize--;
                }
                if (!waitForFc && sepTime > 0)
                {
                    Thread.Sleep(sepTime);
                }
            }
            _lastCanSendTick = Stopwatch.GetTimestamp();
            return true;
        }

        private bool SendBmwfast(byte[] sendData)
        {
            int sendLength = sendData[0] & 0x3F;
            if (sendLength == 0)
            {   // with length byte
                sendLength = sendData[3] + 4;
            }
            else
            {
                sendLength += 3;
            }
            sendData[sendLength] = CalcChecksumBmwFast(sendData, sendLength);
            sendLength++;
            if (!SendData(sendData, sendLength))
            {
                return false;
            }
            return true;
        }

        private bool ReceiveBmwFast(byte[] receiveData)
        {
            // header byte
            if (!ReceiveData(receiveData, 0, 4))
            {
                _serialPort.DiscardInBuffer();
                return false;
            }
            if ((receiveData[0] & 0x80) != 0x80)
            {   // 0xC0: Broadcast
                ReceiveData(receiveData, 0, receiveData.Length);
                _serialPort.DiscardInBuffer();
                return false;
            }
            int recLength = receiveData[0] & 0x3F;
            if (recLength == 0)
            {   // with length byte
                recLength = receiveData[3] + 4;
            }
            else
            {
                recLength += 3;
            }
            if (!ReceiveData(receiveData, 4, recLength - 3))
            {
                _serialPort.DiscardInBuffer();
                return false;
            }
            if (CalcChecksumBmwFast(receiveData, recLength) != receiveData[recLength])
            {
                _serialPort.DiscardInBuffer();
                return false;
            }
            return true;
        }

        static public byte CalcChecksumBmwFast(byte[] data, int length)
        {
            byte sum = 0;
            for (int i = 0; i < length; i++)
            {
                sum += data[i];
            }
            return sum;
        }

        private bool SendKwp2000S(byte[] sendData)
        {
            int sendLength = sendData[3] + 4;
            sendData[sendLength] = CalcChecksumXor(sendData, sendLength);
            sendLength++;
            if (!SendData(sendData, sendLength))
            {
                return false;
            }
            return true;
        }

        private bool ReceiveKwp2000S(byte[] receiveData)
        {
            // header byte
            if (!ReceiveData(receiveData, 0, 4))
            {
                _serialPort.DiscardInBuffer();
                return false;
            }
            int recLength = receiveData[3] + 4;
            if (!ReceiveData(receiveData, 4, recLength - 3))
            {
                _serialPort.DiscardInBuffer();
#if false
                string text = string.Empty;
                for (int i = 0; i < 4; i++)
                {
                    text += string.Format("{0:X02} ", _receiveData[i]);
                }
                Debug.WriteLine("No data: " + text);
#endif
                return false;
            }
            if (CalcChecksumXor(receiveData, recLength) != receiveData[recLength])
            {
                _serialPort.DiscardInBuffer();
#if false
                string text = string.Empty;
                for (int i = 0; i < recLength + 1; i++)
                {
                    text += string.Format("{0:X02} ", _receiveData[i]);
                }
                Debug.WriteLine("Checksum: " + text);
#endif
                return false;
            }
            return true;
        }

        private bool SendDs2(byte[] sendData)
        {
            int sendLength = sendData[1] - 1;
            sendData[sendLength] = CalcChecksumXor(sendData, sendLength);
            sendLength++;
            if (!SendData(sendData, sendLength))
            {
                return false;
            }
            return true;
        }

        private bool ReceiveDs2(byte[] receiveData)
        {
            // header byte
            if (!ReceiveData(receiveData, 0, 2))
            {
                _serialPort.DiscardInBuffer();
                return false;
            }
            int recLength = receiveData[1] - 1;
            if (!ReceiveData(receiveData, 2, recLength - 1))
            {
                _serialPort.DiscardInBuffer();
                return false;
            }
            if (CalcChecksumXor(receiveData, recLength) != receiveData[recLength])
            {
                _serialPort.DiscardInBuffer();
                return false;
            }
            return true;
        }

        static public byte CalcChecksumXor(byte[] data, int length)
        {
            byte sum = 0;
            for (int i = 0; i < length; i++)
            {
                sum ^= data[i];
            }
            return sum;
        }

        private bool SendIso9141Block(byte[] sendData)
        {
            int blockLen = sendData[0];
            byte[] buffer = new byte[1];
            for (int i = 0; i < blockLen; i++)
            {
                if (_stopThread)
                {
                    return false;
                }
                Debug.WriteLine("Send {0:X02}", sendData[i]);
                if (!SendData(sendData, i, 1))
                {
                    return false;
                }
                if (!ReceiveData(buffer, 0, 1, IsoTimeout, IsoTimeout))
                {
                    return false;
                }
                if ((byte)(~buffer[0]) != sendData[i])
                {
                    Debug.WriteLine("Echo incorrect {0:X02}", (byte)(~buffer[0]));
                    return false;
                }
            }
            buffer[0] = 0x03;   // block end
            Debug.WriteLine("Send {0:X02}", buffer[0]);
            if (!SendData(buffer, 0, 1))
            {
                return false;
            }
            return true;
        }

        private bool ReceiveIso9141Block(byte[] recData)
        {
            // block length
            if (!ReceiveData(recData, 0, 1, IsoTimeout, IsoTimeout))
            {
                Debug.WriteLine("Nothing received");
                return false;
            }
            Debug.WriteLine("Rec {0:X02}", recData[0]);

            int blockLen = recData[0];
            byte[] buffer = new byte[1];
            for (int i = 0; i < blockLen; i++)
            {
                if (_stopThread)
                {
                    return false;
                }
                buffer[0] = (byte)~recData[i];
                if (!SendData(buffer, 0, 1))
                {
                    return false;
                }
                if (!ReceiveData(recData, i + 1, 1, IsoTimeout, IsoTimeout))
                {
                    Debug.WriteLine("Nothing received");
                    return false;
                }
                Debug.WriteLine("Rec {0:X02}", recData[i + 1]);
            }
            if (recData[blockLen] != 0x03)
            {
                Debug.WriteLine("Block end invalid {0:X02}", recData[blockLen]);
                return false;
            }
            return true;
        }

        private byte IntToBcd(int value)
        {
            byte result = (byte) ((value % 10) + ((value / 10) << 4));
            return result;
        }

        private void SerialTransmission()
        {
            bool manualMode = false;
            for (int i = 0; i < _timeValveWrite.Length; i++)
            {
                if (_timeValveWrite[i].IsRunning)
                {
                    manualMode = true;
                    if (_timeValveWrite[i].ElapsedMilliseconds > 500)
                    {
                        _outputs &= ~(1 << i);
                        _timeValveWrite[i].Stop();
                    }
                }
            }
            if (_timeIdleSpeedControlWrite.IsRunning)
            {
                if (_timeIdleSpeedControlWrite.ElapsedMilliseconds > 500)
                {
                    _timeIdleSpeedControlWrite.Stop();
                }
            }

            _axisPosPrescaler++;
            if (_axisPosPrescaler > 5)
            {
                _axisPosPrescaler = 0;
                if (!manualMode && _mode == 0x00)
                {
                    if (_axisPosRaw > 0) _axisPosRaw--;
                    if (_axisPosRaw < 0) _axisPosRaw++;
                }
                if (_outputs == 0x07)
                {
                    if (_axisPosRaw > -80) _axisPosRaw--;
                }
                if (_outputs == 0x0B)
                {
                    if (_axisPosRaw < 80) _axisPosRaw++;
                }
                _axisPosFilt = (_axisPosFilt * FilterConst) + ((double)_axisPosRaw * (1 - FilterConst));
            }

            if (VariableValues)
            {
                if (_batteryVoltage > 1200)
                {
                    _batteryVoltage--;
                }
                else
                {
                    _batteryVoltage = 1500;
                }
            }
            else
            {
                _batteryVoltage = 1250;
            }

            if (Moving && _speed < 250)
            {
                _speed++;
            }
            else
            {
                _speed = 0;
            }

            if (_compressorRunningTime < 4000)
            {
                _compressorRunningTime++;
            }
            else
            {
                _compressorRunningTime = 0;
            }

            if (!ObdReceive(_receiveData))
            {
                return;
            }
            int recLength = _receiveData[0] & 0x3F;
            if (recLength == 0)
            {   // with length byte
                recLength = _receiveData[3] + 4;
            }
            else
            {
                recLength += 3;
            }
            recLength += 1; // checksum
#if false
            {
                string text = string.Empty;
                for (int i = 0; i < recLength; i++)
                {
                    text += string.Format("{0:X02} ", _receiveData[i]);
                }
                Debug.WriteLine("Request: " + text);
            }
#endif
            if (!_adsAdapter && !_klineResponder && (_tcpServerDiag == null) && (_pcanHandle == PCANBasic.PCAN_NONEBUS))
            {
                // send echo
                ObdSend(_receiveData);
            }
            if (_noResponseCount > 0)
            {   // no response requested
                _noResponseCount--;
                return;
            }

            bool standardResponse = false;
            if (
                _receiveData[0] == 0x81 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x81)
            {   // start communication service
                int i = 0;
                _sendData[i++] = 0x83;
                _sendData[i++] = 0xF1;
                _sendData[i++] = _receiveData[1];
                _sendData[i++] = 0xC1;
                _sendData[i++] = 0xDF;  // key low
                _sendData[i++] = 0x8F;  // key high

                ObdSend(_sendData);
                Debug.WriteLine("Start communication");
                standardResponse = true;
            }
            else if (
                _receiveData[0] == 0x81 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x3E)
            {   // tester present
                int i = 0;
                _sendData[i++] = 0x83;
                _sendData[i++] = 0xF1;
                _sendData[i++] = _receiveData[1];
                _sendData[i++] = 0x7E;

                ObdSend(_sendData);
                Debug.WriteLine("Tester present");
                standardResponse = true;
            }
            else if (
                _receiveData[0] == 0x81 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x20)
            {   // stop diag
                int i = 0;
                _sendData[i++] = 0x83;
                _sendData[i++] = 0xF1;
                _sendData[i++] = _receiveData[1];
                _sendData[i++] = 0x60;

                ObdSend(_sendData);
                Debug.WriteLine("Stop diag");
                standardResponse = true;
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x14 &&
                _receiveData[4] == 0xFF &&
                _receiveData[5] == 0xFF)
            {   // error reset
                _sendData[0] = 0x83;
                _sendData[1] = 0xF1;
                _sendData[2] = _receiveData[1];
                _sendData[3] = 0x54;
                _sendData[4] = 0xFF;
                _sendData[5] = 0xFF;

                if (!_ecuErrorResetList.Contains(_receiveData[1]))
                {
                    _ecuErrorResetList.Add(_receiveData[1]);
                }
                ObdSend(_sendData);
                standardResponse = true;
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {   // error request
                if (_ecuErrorResetList.Contains(_receiveData[1]))
                {   // disable error response -> send dummy
                    _sendData[0] = 0x82;
                    _sendData[1] = 0xF1;
                    _sendData[2] = _receiveData[1];
                    _sendData[3] = 0x58;
                    _sendData[4] = 0x00;

                    ObdSend(_sendData);
                    standardResponse = true;
                }
            }
#if false
            else if (
                _receiveData[0] == 0x81 &&
                _receiveData[1] == 0x00 &&
                _receiveData[2] == 0x00)
            {   // program CAN adapter
                int i = 0;
                _sendData[i++] = 0x81;
                _sendData[i++] = 0x00;
                _sendData[i++] = 0x00;
                _sendData[i++] = (byte)(~_receiveData[3]);

                ObdSend(_sendData);
                Debug.WriteLine("Program CAN adapter");
                standardResponse = true;
            }
#endif
            bool useResponseList = false;
            if (standardResponse)
            {
                useResponseList = false;
            }
            else
            {
                switch (_responseType)
                {
                   case ResponseType.E61:
                        if (!ResponseE61())
                        {
                            useResponseList = true;
                        }
                        break;

                    case ResponseType.E90:
                        if (!ResponseE90())
                        {
                            useResponseList = true;
                        }
                        break;

                    default:
                        useResponseList = true;
                        break;
                }
            }

            if (useResponseList)
            {
                bool found = false;
                foreach (ResponseEntry responseEntry in _configData.ResponseList)
                {
                    if (recLength != responseEntry.Request.Length) continue;
                    bool equal = true;
                    // ReSharper disable once LoopVariableIsNeverChangedInsideLoop
                    for (int i = 0; i < recLength - 1; i++)
                    {   // don't compare checksum
                        if (_receiveData[i] != responseEntry.Request[i])
                        {
                            equal = false;
                            break;
                        }
                    }
                    if (equal)
                    {       // entry found
                        found = true;
#if false
                        SendData(responseEntry.Response, responseEntry.Response.Length);
#else
                        if (responseEntry.ResponseMultiList.Count > 1)
                        {
                            foreach (byte[] responseTel in responseEntry.ResponseMultiList)
                            {
                                ObdSend(responseTel);
                            }
                        }
                        else
                        {
                            ObdSend(responseEntry.ResponseDyn);
                        }
#endif
                        break;
                    }
                }

                if (!found)
                {
                    if (
                        _receiveData[0] == 0x84 &&
                        _receiveData[2] == 0xF1 &&
                        _receiveData[3] == 0x18 &&
                        _receiveData[4] == 0x02 &&
                        _receiveData[5] == 0xFF &&
                        _receiveData[6] == 0xFF)
                    {
                        // dummy error response for all devices
                        _sendData[0] = 0x82;
                        _sendData[1] = 0xF1;
                        _sendData[2] = _receiveData[1];
                        _sendData[3] = 0x58;
                        _sendData[4] = 0x00;

                        ObdSend(_sendData);
                        found = true;
                    }
                }

                if (!found)
                {
                    string text = string.Empty;
                    for (int i = 0; i < recLength; i++)
                    {
                        text += string.Format("{0:X02} ", _receiveData[i]);
                    }
                    Debug.WriteLine("Not found: " + text);
                }
            }
        }

        private bool ResponseE61()
        {
            // axis unit
            if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0xC1)
            {   // get axis position
                int i = 0;
                int posFilt = (int)Math.Round(_axisPosFilt);
                _sendData[i++] = 0x8A;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x38;
                _sendData[i++] = 0x61;
                _sendData[i++] = 0xC1;
                _sendData[i++] = (byte)_axisPosRaw;         // left fast
                _sendData[i++] = (byte)(_axisPosRaw + 2);   // lest slow
                _sendData[i++] = (byte)posFilt;             // right fast
                _sendData[i++] = (byte)(posFilt + 2);       // rght slow
                _sendData[i++] = 0x00;
                _sendData[i++] = 0x00;
                _sendData[i++] = 0x00;
                _sendData[i++] = 0x00;

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0xC0)
            {   // get voltage values
                int i = 0;
                _sendData[i++] = 0x8D;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x38;
                _sendData[i++] = 0x61;
                _sendData[i++] = 0xC0;
                _sendData[i++] = 0x0E;
                _sendData[i++] = 0x00;
                _sendData[i++] = 0x17;
                _sendData[i++] = 0x00;
                // battery voltage *100
                _sendData[i++] = (byte)_batteryVoltage;
                _sendData[i++] = (byte)(_batteryVoltage >> 8);

                _sendData[i++] = 0xF9;  // sensor voltage *100
                _sendData[i++] = 0x01;

                _sendData[i++] = 0xF7;  // sensor voltage *100
                _sendData[i++] = 0x01;

                _sendData[i++] = 0x4E;

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0xC2)
            {   // digital status
                Array.Copy(_response3821C2, _sendData, _response3821C2.Length);
                _sendData[11] = (byte)((_speed < 10) ? 0x00 : 0x01);   // door contact
                // speed km/h
                _sendData[12] = (byte)_speed;

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0xAC)
            {   // get compressor running time
                int i = 0;
                _sendData[i++] = 0x85;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x38;
                _sendData[i++] = 0x61;
                _sendData[i++] = 0xAC;

                _sendData[i++] = (byte)_compressorRunningTime;
                _sendData[i++] = (byte)(_compressorRunningTime >> 8);
                _sendData[i++] = 0x00;

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x86 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x30 &&
                _receiveData[4] == 0x41 &&
                _receiveData[5] == 0x01)
            {   // get mode
                int i = 0;
                _sendData[i++] = 0x85;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x38;
                _sendData[i++] = 0x70;
                _sendData[i++] = 0x41;
                _sendData[i++] = 0x01;
                _sendData[i++] = 0x00;
                _sendData[i++] = _mode;

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] >= 0x83 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x30 &&
                _receiveData[4] >= 0x11 && _receiveData[4] <= 0x14 &&
                _receiveData[5] == 0x01)
            {   // get valve state
                int channel = _receiveData[4] - 0x11;
                int i = 0;
                _sendData[i++] = 0x85;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x38;
                _sendData[i++] = 0x70;
                _sendData[i++] = _receiveData[4];
                _sendData[i++] = 0x01;
                _sendData[i++] = 0x00;
                _sendData[i++] = (byte)(((_outputs & (1 << channel)) != 0x00) ? 0x01 : 0x00);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x86 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x30 &&
                _receiveData[4] >= 0x11 && _receiveData[4] <= 0x14 &&
                _receiveData[5] == 0x07)
            {   // set valve state
                int channel = _receiveData[4] - 0x11;
                int i = 0;
                _sendData[i++] = 0x86;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x38;
                _sendData[i++] = 0x70;
                _sendData[i++] = _receiveData[4];
                _sendData[i++] = 0x07;
                _sendData[i++] = 0x00;
                _sendData[i++] = _receiveData[7];
                _sendData[i++] = _receiveData[8];

                ObdSend(_sendData);

                _timeValveWrite[channel].Reset();
                _timeValveWrite[channel].Start();
                if ((_receiveData[7] & 0x01) != 0x00)
                {
                    _outputs |= 1 << channel;
                }
                else
                {
                    _outputs &= ~(1 << channel);
                }
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x31 &&
                _receiveData[4] == 0x0C)
            {   // set mode
                int i = 0;
                _sendData[i++] = 0x83;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x38;
                _sendData[i++] = 0x71;
                _sendData[i++] = 0x0C;
                _sendData[i++] = _receiveData[5];

                ObdSend(_sendData);
                switch (_receiveData[5])
                {
                    case 0x00:  // normal
                        if (_mode != 0x00)
                        {
                            _noResponseCount = 1;
                        }
                        _mode = 0x00;
                        break;

                    case 0x01:  // conveyor mode
                        if (_mode != 0x02)
                        {
                            _noResponseCount = 1;
                        }
                        _mode = 0x02;
                        break;

                    case 0x02:  // transport mode
                        if (_mode != 0x04)
                        {
                            _noResponseCount = 1;
                        }
                        _mode = 0x04;
                        break;

                    case 0x04:  // garage mode
                        if (_mode != 0x40)
                        {
                            _noResponseCount = 1;
                        }
                        _mode = 0x40;
                        break;
                }
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {   // read error memory for DIS
                Array.Copy(_response381802FFFF, _sendData, _response381802FFFF.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x17 &&
                _receiveData[4] == 0x5F &&
                _receiveData[5] == 0xB4)
            {
                Array.Copy(_response38175FB4, _sendData, _response38175FB4.Length);

                // Fehlerhäufigkeit
                _sendData[8] = 3;

                // Logistikzähler
                _sendData[9] = 20;

                // Kilometerstand
                // 0xFFFF: 524280 = 0x7FFF8 (Left shift 3)
                int intValue = (int)(123456 >> 3);
                _sendData[10] = (byte)(intValue >> 8);
                _sendData[11] = (byte)(intValue);

                // Unbenutzte UW
                intValue = 0x1234;
                _sendData[12] = (byte)(intValue >> 8);
                _sendData[13] = (byte)(intValue);

                intValue = 0x2345;
                _sendData[14] = (byte)(intValue >> 8);
                _sendData[15] = (byte)(intValue);

                intValue = 0x3456;
                _sendData[16] = (byte)(intValue >> 8);
                _sendData[17] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {   // standard response 1A80 for INPA
                Array.Copy(_response381A80, _sendData, _response381A80.Length);
#if false
                // production date
                _sendData[17] = 0x20;
                _sendData[18] = 0x10;
                _sendData[19] = 0x05;
                _sendData[20] = 0x11;
#endif
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x38 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0x30)
            {   // standard response 2230 for INPA
                Array.Copy(_response382230, _sendData, _response382230.Length);

                ObdSend(_sendData);
            }
            // motor unit DDE6.0 for M47 TÜ2
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x31 &&
                _receiveData[4] == 0x85)
            {   // set LL controller
                _idleSpeedControl = _receiveData[5];
                _timeIdleSpeedControlWrite.Reset();
                _timeIdleSpeedControlWrite.Start();

                int i = 0;
                _sendData[i++] = 0x83;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x12;
                _sendData[i++] = 0x71;
                _sendData[i++] = 0x85;
                _sendData[i++] = (byte)_idleSpeedControl;

                ObdSend(_sendData);
            }
            else if (
                (_receiveData[0] & 0xC0) == 0x80 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x2C &&
                _receiveData[4] == 0x10)
            {   // request list
                int i = 0;
                _sendData[i++] = 0x82;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x12;
                _sendData[i++] = 0x6C;
                _sendData[i++] = 0x10;

                int items = ((_receiveData[0] & 0x3F) - 2) / 2;
                if (items == 0)
                {   // use last request data
                    if (_receiveDataMotorBackup[1] == 0x12)
                    {
                        _receiveDataMotorBackup.CopyTo(_receiveData, 0);
                        items = ((_receiveData[0] & 0x3F) - 2) / 2;
                    }
                }
                else
                {
                    _receiveData.CopyTo(_receiveDataMotorBackup, 0);
                }
                for (int j = 0; j < items; j++)
                {
                    int itemAddr = ((int)_receiveData[5 + j * 2] << 8) + _receiveData[6 + j * 2];
                    long itemValue = 0x000;
                    int resultBytes = 2;
                    switch (itemAddr)
                    {
                        case 0x0005:    // motor / refrigerant temp
                            // temp [C] + 41.08
                            itemValue = (long)(50 + 41.08);
                            break;

                        case 0x0080:    // Luftmasse
                            // 0x0000 == -1600
                            // 0x7FFF == 0
                            // 0xFFFF == +1600
                            // (air * 10 * 0xFFFF / 3200) + 0x7FFF;
                            itemValue = (350 * 0xFFFF / 3200) + 0x7FFF;
                            break;

                        case 0x0081:    // Luftmasse ist
                            // 0x0000 == -1600
                            // 0x8000 == 0
                            // 0xFFFF == 1600
                            // (lm [mg] + 1600) * 0xFFFF / 3200
                            itemValue = (527 + 1600) * 0xFFFF / 3200;
                            break;

                        case 0x0089:    // PWG 1 (Pedalwertgeber)
                            // 0x0000 == 0 V
                            // 0x1FFF == 5 V
                            // pwg [V] * 0x1FFF / 5
                            itemValue = (long)(3.5 * 0x1FFF / 5);
                            break;

                        case 0x008A:    // PWG 2 (Pedalwertgeber)
                            // 0x0000 == 0 V
                            // 0x1FFF == 5 V
                            // pwg [V] * 0x1FFF / 5
                            itemValue = (long)(1.3 * 0x1FFF / 5);
                            break;

                        case 0x008B:    // Umgebungsdruck
                            // 0x0000 == 0 mbar
                            // 0x8000 == 4096 mbar
                            // Druck [mbar] * 0x8000 / 4096
                            itemValue = (long)(935 * 0x8000 / 4096);
                            break;

                        case 0x008D:    // Luftmasse soll
                            // 0x0000 == -1600
                            // 0x8000 == 0
                            // 0xFFFF == 1600
                            // (lm [mg] + 1600) * 0xFFFF / 3200
                            itemValue = (523 + 1600) * 0xFFFF / 3200;
                            break;

                        case 0x0091:    // Ladedruck ist
                            // 0x0000 == 0
                            // 0x8000 == 4096
                            // Druck [mbar] * 0x8000 / 4096
                            itemValue = (long)(1935 * 0x8000 / 4096);
                            break;

                        case 0x0093:    // battery voltage
                            // 0x7F3C = 80.00 V
                            // bat * 100 * 0x7F3C / 8000
                            itemValue = _batteryVoltage * 0x7F3C / 8000;
                            break;

                        case 0x0095:    // refrigerant temp
                            // temp [C] + 41.08
                            // 0x0000 == -50.1
                            // 0x8000 == 499.9
                            // (temp [C] + 50.1) * 0x8000 / 550.0
                            itemValue = (long)((50.0 + 50.1) * 0x8000 / 550.0);
                            break;

                        case 0x009B:    // Partikelfilter Status Regeneration
                            // 0,1 == inaktiv
                            // >=2 == aktiv
                            itemValue = 0x0002;
                            break;

                        case 0x009E:    // motor rpm
                            // rpm * 8
                            itemValue = 400 * 8;
                            break;

                        case 0x00A0:    // Kraftstofftemperatur
                            // 0x0000 == -50.1
                            // 0x8000 == 499.9
                            // (temp [C] + 50.1) * 0x8000 / 550.0
                            itemValue = (long)((40.3 + 50.1) * 0x8000 / 550.0);
                            break;

                        case 0x00AD:    // intake air temp
                            // 0x0000 == -50.1
                            // 0x8000 == 499.9
                            // (temp [C] + 50.1) * 0x8000 / 550.0
                            itemValue = (long)((80 + 50.1) * 0x8000 / 550.0);
                            break;

                        case 0x00AE:    // Ladelufttemp
                            // 0x0000 == -50.1
                            // 0x8000 == 499.9
                            // (temp [C] + 50.1) * 0x8000 / 550.0
                            itemValue = (long)((60.1 + 50.1) * 0x8000 / 550.0);
                            break;

                        case 0x00C6:    // Ladedruck soll
                            // 0x0000 == 0
                            // 0x8000 == 4096
                            // Druck [mbar] * 0x8000 / 4096
                            itemValue = (long)(1938 * 0x8000 / 4096);
                            break;

                        case 0x00BF:    // Öldruckschalter
                            itemValue = 0x0001;
                            break;

                        case 0x00C2:    // Abgastemp vor Kat
                            // 0x0000 == -51.1
                            // 0x8000 == 32724.9
                            // (temp [C] + 51.1) * 0x8000 / 32776.0
                            itemValue = (long)((175.3 + 51.1) * 0x8000 / 32776);
                            break;

                        case 0x00CA:    // Abgastemp vor Partikelfilter
                            // 0x0000 == -51.1
                            // 0x8000 == 32724.9
                            // (temp [C] + 51.1) * 0x8000 / 32776.0
                            itemValue = (long)((165.3 + 51.1) * 0x8000 / 32776);
                            break;

                        case 0x00D1:    // Partikelfilter Strecke seit Regeneration
                            // 0x00 == 0m
                            // 0xFF == 32640m
                            // Strecke [m] >> 7
                            itemValue = 145678 >> 7;
                            break;

                        case 0x00D8:    // Abgasgegendruck
                            // 0 == 0m
                            // 0x8000 == 4096 mbar
                            // Druck [mbar] * 0x8000 / 4096
                            itemValue = (long)(2943 * 0x8000 / 4096);
                            break;

                        case 0x00DD:    // Partikelfilter Freigabe Regeneration
                            // 0 == freigegeben
                            // 1 == gesperrt
                            itemValue = 0x0000;
                            break;

                        case 0x00DF:    // Raildruck ist
                            // 0x0000 == 0
                            // 0x8000 == 1000
                            // Druck [mbar] * 0x8000 / 1000
                            itemValue = (long)(1027 * 0x8000 / 1000);
                            break;

                        case 0x00E1:    // Raildruck soll
                            // 0x0000 == 0
                            // 0x8000 == 1000
                            // Druck [mbar] * 0x8000 / 1000
                            itemValue = (long)(1024 * 0x8000 / 1000);
                            break;

                        case 0x13EC:    // Bremslichtschalter
                            itemValue = 0x0001;
                            break;

                        case 0x13ED:    // Bremslichttestschalter
                            itemValue = 0x0001;
                            break;

                        case 0x146E:    // Kupplungsschalter
                            itemValue = 0x0001;
                            break;

                        case 0x1482:    // FGR Bedienteil
                            itemValue = 0x006F; // 0x01: +, 0x02: -, 0x04: Wieder, 0x08: Aus, 0x20: +über, 0x40: -über
                            break;

                        case 0x15E4:    // Klimaschalter
                            itemValue = 0x0001;
                            break;

                        case 0x15E5:    // Umgebungstemperatur
                            // 0x0000 == -50.1
                            // 0x8000 == 499.9
                            // (temp [C] + 50.1) * 0x8000 / 550.0
                            itemValue = (long)((35.4 + 50.1) * 0x8000 / 550.0);
                            break;

                        case 0x1645:    // Getriebeart
                            itemValue = 0x0000; // (0=Hand, 1=Auto)
                            break;

                        case 0x1770:    // Drehung Zylinder 1
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x01))
                            {
                                break;
                            }
                            // 0x0000 = 0
                            // 0xFFFF = 8192
                            // (speed [rpm] -100) * 0xFFFF / 8192
                            itemValue = (long)(123.4 * 0xFFFF / 8192);
                            break;

                        case 0x1771:    // Drehung Zylinder 2
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x01))
                            {
                                break;
                            }
                            // 0x0000 = 0
                            // 0xFFFF = 8192
                            // (speed [rpm] -100) * 0xFFFF / 8192
                            itemValue = (long)(234.5 * 0xFFFF / 8192);
                            break;

                        case 0x1772:    // Drehung Zylinder 3
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x01))
                            {
                                break;
                            }
                            // 0x0000 = 0
                            // 0xFFFF = 8192
                            // (speed [rpm] -100) * 0xFFFF / 8192
                            itemValue = (long)(345.6 * 0xFFFF / 8192);
                            break;

                        case 0x1773:    // Drehung Zylinder 4
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x01))
                            {
                                break;
                            }
                            // 0x0000 = 0
                            // 0xFFFF = 8192
                            // (speed [rpm] -100) * 0xFFFF / 8192
                            itemValue = (long)(456.7 * 0xFFFF / 8192);
                            break;

                        case 0x177A:    // Mengenkorrektur Zylinder 1
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x00))
                            {
                                break;
                            }
                            // 0x0000 = -100
                            // 0xFFFF = 100
                            // (Mkorr [mg/Hub] + 100) * 0xFFFF / 200
                            itemValue = (long)((3.45 + 100) * 0xFFFF / 200);
                            break;

                        case 0x177B:    // Mengenkorrektur Zylinder 2
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x00))
                            {
                                break;
                            }
                            // 0x0000 = -100
                            // 0xFFFF = 100
                            // (Mkorr [mg/Hub] + 100) * 0xFFFF / 200
                            itemValue = (long)((1.23 + 100) * 0xFFFF / 200);
                            break;

                        case 0x177C:    // Mengenkorrektur Zylinder 3
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x00))
                            {
                                break;
                            }
                            // 0x0000 = -100
                            // 0xFFFF = 100
                            // (Mkorr [mg/Hub] + 100) * 0xFFFF / 200
                            itemValue = (long)((-4.56 + 100) * 0xFFFF / 200);
                            break;

                        case 0x177D:    // Mengenkorrektur Zylinder 4
                            if (!_timeIdleSpeedControlWrite.IsRunning || (_idleSpeedControl != 0x00))
                            {
                                break;
                            }
                            // 0x0000 = -100
                            // 0xFFFF = 100
                            // (Mkorr [mg/Hub] + 100) * 0xFFFF / 200
                            itemValue = (long)((-1.45 + 100) * 0xFFFF / 200);
                            break;

                        case 0x1952:    // Partikelfilter Anforderung Regeneration
                            // 0 == angefordert
                            // 1 == nicht angefordert
                            itemValue = 0x0000;
                            break;
                    }
                    if (resultBytes >= 4) _sendData[i++] = (byte)(itemValue >> 24);
                    if (resultBytes >= 3) _sendData[i++] = (byte)(itemValue >> 16);
                    if (resultBytes >= 2) _sendData[i++] = (byte)(itemValue >> 8);
                    if (resultBytes >= 1) _sendData[i++] = (byte)(itemValue);
                }
                _sendData[0] = (byte)(0x80 | (i - 3));

                //Thread.Sleep(2000);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {   // motor error log summary
                Array.Copy(_response121802FFFF, _sendData, _response121802FFFF.Length);

                _sendData[0] = 0x88;
                _sendData[4] = 0x02;
                _sendData[8] = 0x42;
                _sendData[9] = 0x22;
                _sendData[10] = 0x24;

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x17 &&
                _receiveData[4] == 0x42 &&
                _receiveData[5] == 0x32)
            {   // motor error log detail for error 4232
                // digit 3 = zylinder numer
                // digit 4 = error type (2=disrupted)
                Array.Copy(_response12174232, _sendData, _response12174232.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x17 &&
                _receiveData[4] == 0x42 &&
                _receiveData[5] == 0x22)
            {   // motor error log detail for error 4222
                // digit 3 = zylinder numer
                // digit 4 = error type (2=disrupted)
                Array.Copy(_response12174232, _sendData, _response12174232.Length);

                _sendData[6] = 0x22;

                // Fehlerart
                // Bit 0-3:
                // 0: Kein passendes Fehlersymptom
                // 1: Signal oder Wert oberhalb der Schwelle
                // 2: Signal oder Wert unterhalb der Schwelle
                // 3: Unbekannte Fehlerart
                // 4: Kein Signal oder Wert
                // 5-7: Unbekannte Fehlerart
                // 8: Unplausibles Signal oder Wert
                // 9-15: Unbekannte Fehlerart
                //
                // Bit 4: 1=Testbedingung noch nicht erfüllt
                //
                // Bit 5-6:
                // 0: Fehler bisher nicht aufgetreten
                // 1: Fehler momentan nicht vorhanden, aber bereits gespeichert
                // 2: Fehler momentan vorhanden, aber noch nicht gespeichert
                // 3: Fehler momentan vorhanden, und bereits gespeichert
                //
                // Bit 7: 1=Fehler würde das Aufleuchten einer Warnlampe verursachen
                _sendData[7] = 0x24;

                // Status
                // Bit 0: 1=Fehler in Shadow eingetragen
                // Bit 1: 1=Block 1 gültig
                // Bit 2: 1=Block 1 gültig
                _sendData[8] = 0x07;

                // Fehlerdetail
                int intValue = 0x1234;
                _sendData[9] = (byte)(intValue >> 8);
                _sendData[10] = (byte)(intValue);

                // Fehlerhäufigkeit
                _sendData[11] = 10;

                // Logistikzähler
                _sendData[12] = 50;

                // Block1
                // Kilometerstand
                // 0xFFFF: 524280 = 0x7FFF8 (Left shift 3)
                intValue = (int)(123456 >> 3);
                _sendData[13] = (byte)(intValue >> 8);
                _sendData[14] = (byte)(intValue);

                // Motordrehzahl
                // 0xFF: 7033.54
                intValue = (int)(1000.0 * 0xFF / 7033.54);
                _sendData[15] = (byte)intValue;

                // Kühlmitteltemperatur
                // 0x00: -50.27
                // 0xFF: 205.73
                // (temp [C] + 50.27)
                intValue = (int)(100.0 + 50.27);
                _sendData[16] = (byte)intValue;

                // Raildruck ist
                // 0x00: 0
                // 0xFF: 2008.62
                intValue = (int)(1500.0 * 0xFF / 2008.62);
                _sendData[17] = (byte)intValue;

                // Einspritzmenge
                // 0x00: 0
                // 0xFF: 100.43
                intValue = (int)(50.0 * 0xFF / 100.43);
                _sendData[18] = (byte)intValue;

                // Luftmasse pro Zylinder
                // 0x00: 0
                // 0xFF: 1606.89
                intValue = (int)(1000.0 * 0xFF / 1606.89);
                _sendData[19] = (byte)intValue;

                // Ladedruck Istwert
                // 0x00: 0
                // 0xFF: 2510.77
                intValue = (int)(2000.0 * 0xFF / 2510.77);
                _sendData[20] = (byte)intValue;

                // Pedalwertgeber
                // 0x00: 0
                // 0xFF: 200.79
                intValue = (int)(80.0 * 0xFF / 200.79);
                _sendData[21] = (byte)intValue;

                // Batteriespannung
                // 0xFF: 41546.17
                intValue = (int)(12000.0 * 0xFF / 41546.17);
                _sendData[22] = (byte)intValue;

                // Geschwindigkeit
                // 0xFF: 251.68
                intValue = (int)(100.0 * 0xFF / 251.68);
                _sendData[23] = (byte)intValue;

                // Zustand Glühanzeige
                _sendData[24] = 210;

                // Block 2
                // Kilometerstand
                // 0xFFFF: 524280 = 0x7FFF8 (Left shift 3)
                intValue = (int)(234567 >> 3);
                _sendData[25] = (byte)(intValue >> 8);
                _sendData[26] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0x20 &&
                _receiveData[5] == 0x00)
            {   // motor info log
                Array.Copy(_response12222000, _sendData, _response12222000.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {   // standard response 1A80 for INPA
                Array.Copy(_response121A80, _sendData, _response121A80.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x94)
            {   // standard response 1A94 for DIS
                Array.Copy(_response121A94, _sendData, _response121A94.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0x20)
            {   // standard response 2120 for DIS
                Array.Copy(_response122120, _sendData, _response122120.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x86 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x23 &&
                _receiveData[4] == 0x00 &&
                _receiveData[5] == 0x00 &&
                _receiveData[6] == 0x00 &&
                _receiveData[7] == 0x07 &&
                _receiveData[8] == 0x40)
            {   // standard response 230000000740 for DIS
                Array.Copy(_response12230000000740, _sendData, _response12230000000740.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x86 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x23 &&
                _receiveData[4] == 0x00 &&
                _receiveData[5] == 0x00 &&
                _receiveData[6] == 0x40 &&
                _receiveData[7] == 0x07 &&
                _receiveData[8] == 0x40)
            {   // standard response 230000000740 for DIS
                Array.Copy(_response12230000400740, _sendData, _response12230000400740.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0x40 &&
                _receiveData[5] == 0x21)
            {   // PM ident
                Array.Copy(_response12224021, _sendData, _response12224021.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0x40 &&
                _receiveData[5] == 0x22)
            {   // PM info 1/3
#if false
                Array.Copy(_response12Nr, _sendData, _response12Nr.Length);
                ObdSend(_sendData);
                Thread.Sleep(1000);
#endif
                Array.Copy(_response12224022, _sendData, _response12224022.Length);

                // Batterieentladung gesamt Ah
                // 0x0000 = 0
                // 0xFFFF = 19088.16
                // Bat [Ah] * 0xFFFF / 19088.16
                int intValue = (int)(1345.6 * 0xFFFF / 19088.16);
                _sendData[7] = (byte)(intValue >> 8);
                _sendData[8] = (byte)(intValue);

                // Batterieladung gesamt Ah
                // 0x0000 = 0
                // 0xFFFF = 19088.16
                // Bat [Ah] * 0xFFFF / 19088.16
                intValue = (int)(1456.7 * 0xFFFF / 19088.16);
                _sendData[9] = (byte)(intValue >> 8);
                _sendData[10] = (byte)(intValue);

                // Stunden im Ladungsbereich
                // 0-20
                intValue = 4567;
                _sendData[11] = (byte)(intValue >> 8);
                _sendData[12] = (byte)(intValue);
                // 20-40
                intValue = 5678;
                _sendData[13] = (byte)(intValue >> 8);
                _sendData[14] = (byte)(intValue);
                // 40-60
                intValue = 6789;
                _sendData[15] = (byte)(intValue >> 8);
                _sendData[16] = (byte)(intValue);
                // 60-80
                intValue = 7890;
                _sendData[17] = (byte)(intValue >> 8);
                _sendData[18] = (byte)(intValue);
                // 80-100
                intValue = 8901;
                _sendData[19] = (byte)(intValue >> 8);
                _sendData[20] = (byte)(intValue);

                // Minuten bei Temp
                // 0x0000 = 0
                // 0xFFFF = 327675
                // Bat [Ah] * 0xFFFF / 19088.16
                // < 0
                intValue = (int)(1485 * 0xFFFF / 327675);
                _sendData[21] = (byte)(intValue >> 8);
                _sendData[22] = (byte)(intValue);
                // 0-20
                intValue = (int)(1357 * 0xFFFF / 327675);
                _sendData[23] = (byte)(intValue >> 8);
                _sendData[24] = (byte)(intValue);
                // 20-40
                intValue = (int)(3579 * 0xFFFF / 327675);
                _sendData[25] = (byte)(intValue >> 8);
                _sendData[26] = (byte)(intValue);
                // 40-60
                intValue = (int)(5791 * 0xFFFF / 327675);
                _sendData[27] = (byte)(intValue >> 8);
                _sendData[28] = (byte)(intValue);
                // > 60
                intValue = (int)(7913 * 0xFFFF / 327675);
                _sendData[29] = (byte)(intValue >> 8);
                _sendData[30] = (byte)(intValue);

                // Km Stand
                // Heute
                intValue = 123;
                _sendData[31] = (byte)(intValue >> 8);
                _sendData[32] = (byte)(intValue);
                // Vor 1 Tag
                intValue = 1234;
                _sendData[33] = (byte)(intValue >> 8);
                _sendData[34] = (byte)(intValue);
                // Vor 2 Tagen
                intValue = 12345;
                _sendData[35] = (byte)(intValue >> 8);
                _sendData[36] = (byte)(intValue);
                // Vor 3 Tagen
                intValue = 234;
                _sendData[37] = (byte)(intValue >> 8);
                _sendData[38] = (byte)(intValue);
                // Vor 4 Tagen
                intValue = 2345;
                _sendData[39] = (byte)(intValue >> 8);
                _sendData[40] = (byte)(intValue);
                // Vor 5 Tagen
                intValue = 23456;
                _sendData[41] = (byte)(intValue >> 8);
                _sendData[42] = (byte)(intValue);

                // Letzter Batterietausch
                intValue = 18346;
                _sendData[43] = (byte)(intValue >> 8);
                _sendData[44] = (byte)(intValue);
                intValue = 17346;
                _sendData[45] = (byte)(intValue >> 8);
                _sendData[46] = (byte)(intValue);
                intValue = 16346;
                _sendData[47] = (byte)(intValue >> 8);
                _sendData[48] = (byte)(intValue);
                intValue = 15346;
                _sendData[49] = (byte)(intValue >> 8);
                _sendData[50] = (byte)(intValue);

                // Batterieentladung während Motorlauf
                // 0x0000 = 0
                // 0xFFFF = 19088.16
                // Bat [Ah] * 0xFFFF / 19088.16
                intValue = (int)(4796.5 * 0xFFFF / 19088.16);
                _sendData[51] = (byte)(intValue >> 8);
                _sendData[52] = (byte)(intValue);

                // Ruhestromverletzung
                // 0x0 = kein Ruhestrom
                // 0x1 = 80-200mA
                // 0x2 = 200mA-1000mA
                // 0x3 = > 1000mA
                // 0x04 : Licht
                // 0x08 : Standheizung
                // 0x0C : Sonstige
                _sendData[54] = 0x01;
                _sendData[53] = 0x23;
                _sendData[56] = 0x48 | 0x11;
                _sendData[55] = 0xC0 | 0x11;
                _sendData[58] = 0x48 | 0x22;
                _sendData[57] = 0xC0 | 0x22;

                _sendData[66] = 0x48 | 0x33;
                _sendData[65] = 0xC0 | 0x33;

                // IBS Fehler Parity
                intValue = 10045;
                _sendData[69] = (byte)(intValue >> 8);
                _sendData[70] = (byte)(intValue);

                // IBS Fehler Watchdog Reset
                intValue = 10046;
                _sendData[71] = (byte)(intValue >> 8);
                _sendData[72] = (byte)(intValue);

                // IBS Fehler Power on Reset
                intValue = 10047;
                _sendData[73] = (byte)(intValue >> 8);
                _sendData[74] = (byte)(intValue);

                // KTBS Fehler BSD erweitert
                intValue = 20031;
                _sendData[75] = (byte)(intValue >> 8);
                _sendData[76] = (byte)(intValue);

                // KTIBS Fehler BSD
                intValue = 20032;
                _sendData[77] = (byte)(intValue >> 8);
                _sendData[78] = (byte)(intValue);

                // KTIBS Fehler EBSD Checksumme
                intValue = 20033;
                _sendData[79] = (byte)(intValue >> 8);
                _sendData[80] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0x40 &&
                _receiveData[5] == 0x23)
            {   // PM info 2
                Array.Copy(_response12224023, _sendData, _response12224023.Length);
#if true

                // Battery capacity
                // 0x00 = 0
                // 0xFF = 255
                // Bat [Ah]
                _sendData[6] = 95;

                // SOH State of Health
                // 0x00 = 0
                // 0x7F = +50
                // 0xFF = -50
                // bit 7 : 1= neg
                // (SOH * 0x7F / 50.0) + ((SOH < 0) ? 0x80 : 0x00)
                double value = -45.0;
                _sendData[7] = (byte)((Math.Abs(value) * 0x7F / 50.0) + ((value < 0) ? 0x80 : 0x00));

                // SOC Fit
                // 0x00 = 0
                // 0xFF = 100.0
                // fit [%] * 0xFF / 100.0
                _sendData[8] = (byte)(90.0 * 0xFF / 100.0);

                // Saison temp
                // 0x00 = 0
                // 0x7F = +127.0
                // 0xFF = -127.0
                // bit 7 : 1= neg
                // (temp [°C] * 0x7F / 127.0) + ((temp < 0) ? 0x80 : 0x00)
                value = +23.0;
                _sendData[9] = (byte)((Math.Abs(value) * 0x7F / 127.0) + ((value < 0) ? 0x80 : 0x00));

                // Kalibrierereignisse
                _sendData[10] = 5;

                // Ah Q SOC
                // 0x00 = 0
                // 0xFF = 1188.3
                // Q [Ah] * 0xFF / 1188.3
                _sendData[11] = (byte)(300.0 * 0xFF / 1188.3);
                _sendData[12] = (byte)(400.0 * 0xFF / 1188.3);
                _sendData[13] = (byte)(500.0 * 0xFF / 1188.3);
                _sendData[14] = (byte)(600.0 * 0xFF / 1188.3);
                _sendData[15] = (byte)(700.0 * 0xFF / 1188.3);
                _sendData[16] = (byte)(800.0 * 0xFF / 1188.3);

                // Startfähigkeit
                // 0x00 = 0
                // 0xFF = 100.0
                // start [%] * 0xFF / 100.0
                _sendData[17] = (byte)(10.0 * 0xFF / 100.0);
                _sendData[18] = (byte)(20.0 * 0xFF / 100.0);
                _sendData[19] = (byte)(30.0 * 0xFF / 100.0);
                _sendData[20] = (byte)(40.0 * 0xFF / 100.0);
                _sendData[21] = (byte)(50.0 * 0xFF / 100.0);
                _sendData[22] = (byte)(60.0 * 0xFF / 100.0);

                // Ladungszustand
                // 0x00 = 0
                // 0xFF = 100.0
                // start [%] * 0xFF / 100.0
                _sendData[23] = (byte)(20.0 * 0xFF / 100.0);
                _sendData[24] = (byte)(30.0 * 0xFF / 100.0);
                _sendData[25] = (byte)(40.0 * 0xFF / 100.0);
                _sendData[26] = (byte)(50.0 * 0xFF / 100.0);
                _sendData[27] = (byte)(60.0 * 0xFF / 100.0);
                _sendData[28] = (byte)(70.0 * 0xFF / 100.0);

                // IBS Intelligent Battery sensor
                // IBS error download checksum
                _sendData[29] = 35;
                // IBS error EEPROM diag
                _sendData[30] = 8;
                // IBS error RAM diag
                _sendData[31] = 9;
                // IBS error PROM diag
                _sendData[32] = 16;
                // IBS error I2C NAC
                _sendData[33] = 1;
                // IBS error Bus Coll
                _sendData[34] = 2;
#endif
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {   // read error memory
                Array.Copy(_responseA01802FFFF, _sendData, _responseA01802FFFF.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {   // CCC nav
                Array.Copy(_responseA01A80p1, _sendData, _responseA01A80p1.Length);
                ObdSend(_sendData);

                Array.Copy(_responseA01A80p2, _sendData, _responseA01A80p2.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0x20 &&
                _receiveData[5] == 0x00)
            {   // CCC nav
                Array.Copy(_responseA0222000, _sendData, _responseA0222000.Length);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x21)
            {   // CCC nav tacho pulses
                Array.Copy(_responseA022F121, _sendData, _responseA022F121.Length);

                // GYRO status
                // 0=OK
                _sendData[6] = 0;

                // tacho pulses
                int intValue = 312;
                _sendData[8] = (byte)(intValue >> 8);
                _sendData[9] = (byte)(intValue);

                // gear rate
                // 0x4000 : 20V
                // 0x0000 : 0V
                intValue = (int)(7.854 * 0x4000 / 20.0);
                _sendData[10] = (byte)(intValue >> 8);
                _sendData[11] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x20)
            {   // CCC nav GPS status
                Array.Copy(_responseA022F120p1, _sendData, _responseA022F120p1.Length);
                ObdSend(_sendData);

                Array.Copy(_responseA022F120p2, _sendData, _responseA022F120p2.Length);

                // HIP driver
                // 0=OK
                // 1=No data
                _sendData[6] = 0;

                // GPS status
                // 0=search
                // 1=tracking
                // 2=2D
                // 3=3D
                _sendData[7] = 3;

                // Almanach
                // 0=Not OK
                // 1=OK
                _sendData[8] = 1;

                // 4000 delay are accepted by INPA
                //Thread.Sleep(4000);
                // test for CarControl
                //Thread.Sleep(750);
                Thread.Sleep(300);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x22)
            {   // CCC nav, Self test GPS
                Array.Copy(_responseA022F122p1, _sendData, _responseA022F122p1.Length);
                ObdSend(_sendData);

                Array.Copy(_responseA022F122p2, _sendData, _responseA022F122p2.Length);

                // Self test GPS
                // 0=OK
                // 1=Not connected
                // 2=Short circuit
                int intValue = 0;
                _sendData[6] = (byte)(intValue >> 8);
                _sendData[7] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x23)
            {   // CCC nav position
                Array.Copy(_responseA022F123, _sendData, _responseA022F123.Length);

                // GPS data valid
                // 0=OK
                _sendData[6] = 0;

                // Position latitude
                // max = +0x7FFFFFFF;
                // min = -0x7FFFFFFF;
                // 180 Grad, 60 min, 60 sec, Rest ms (180 * 60 * 60 * 1000)
                long longValue = 45 * 60 * 60 * 1000 + 32 * 60 * 1000 + 56 * 1000 + 764;
                int intValue = (int)(longValue * 0x7FFFFFFF / (180 * 60 * 60 * 1000));
                _sendData[7] = (byte)(intValue >> 24);
                _sendData[8] = (byte)(intValue >> 16);
                _sendData[9] = (byte)(intValue >> 8);
                _sendData[10] = (byte)(intValue);

                // Position longitude
                // max = +0x7FFFFFFF;
                // min = -0x7FFFFFFF;
                // 180 Grad, 60 min, 60 sec, Rest ms (180 * 60 * 60 * 1000)
                longValue = -(42 * 60 * 60 * 1000 + 24 * 60 * 1000 + 53 * 1000 + 876);
                intValue = (int)(longValue * 0x7FFFFFFF / (180 * 60 * 60 * 1000));
                _sendData[11] = (byte)(intValue >> 24);
                _sendData[12] = (byte)(intValue >> 16);
                _sendData[13] = (byte)(intValue >> 8);
                _sendData[14] = (byte)(intValue);

                // Height NN
                intValue = 350;
                _sendData[15] = (byte)(intValue >> 24);
                _sendData[16] = (byte)(intValue >> 16);
                _sendData[17] = (byte)(intValue >> 8);
                _sendData[18] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x24)
            {   // CCC nav dr position
                Array.Copy(_responseA022F124, _sendData, _responseA022F124.Length);

                // Position latitude
                // max = +0x7FFFFFFF;
                // min = -0x7FFFFFFF;
                // 180 Grad, 60 min, 60 sec, Rest ms (180 * 60 * 60 * 1000)
                long longValue = 45 * 60 * 60 * 1000 + 32 * 60 * 1000 + 56 * 1000 + 764;
                int intValue = (int)(longValue * 0x7FFFFFFF / (180 * 60 * 60 * 1000));
                _sendData[7] = (byte)(intValue >> 24);
                _sendData[8] = (byte)(intValue >> 16);
                _sendData[9] = (byte)(intValue >> 8);
                _sendData[10] = (byte)(intValue);

                // Position longitude
                // max = +0x7FFFFFFF;
                // min = -0x7FFFFFFF;
                // 180 Grad, 60 min, 60 sec, Rest ms (180 * 60 * 60 * 1000)
                longValue = -(42 * 60 * 60 * 1000 + 24 * 60 * 1000 + 53 * 1000 + 876);
                intValue = (int)(longValue * 0x7FFFFFFF / (180 * 60 * 60 * 1000));
                _sendData[11] = (byte)(intValue >> 24);
                _sendData[12] = (byte)(intValue >> 16);
                _sendData[13] = (byte)(intValue >> 8);
                _sendData[14] = (byte)(intValue);

                // Height NN
                intValue = 350;
                _sendData[15] = (byte)(intValue >> 24);
                _sendData[16] = (byte)(intValue >> 16);
                _sendData[17] = (byte)(intValue >> 8);
                _sendData[18] = (byte)(intValue);

                // Status position 1 = OK
                _sendData[19] = 1;

                // Geschwindigkeit
                // 0xFFFF: 2359.2123360 km/h
                // 0x0000: 0 km/h
                intValue = (int) (100 * 0xFFFF / 2359.2123360);
                _sendData[20] = (byte)(intValue >> 8);
                _sendData[21] = (byte)(intValue);

                // Status Geschwindigkeit 1 = OK
                _sendData[22] = 1;

                // Richtung
                // 0x7FF8: 180 Grad
                // 0x0000: 0 km/h
                longValue = (57 * 60 * 60 + 48 * 60 + 53);
                intValue = (int)(longValue * 0x7FF8 / (180 * 60 * 60));
                _sendData[23] = (byte)(intValue >> 8);
                _sendData[24] = (byte)(intValue);

                // Status Richtung 1 = OK
                _sendData[25] = 1;

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x25)
            {   // CCC nav resolution
                Array.Copy(_responseA022F125, _sendData, _responseA022F125.Length);

                // Vertikale Auflösung
                int intValue = 1234;
                _sendData[7] = (byte)(intValue >> 8);
                _sendData[8] = (byte)(intValue);

                // Horizontale Auflösung
                intValue = 2345;
                _sendData[9] = (byte)(intValue >> 8);
                _sendData[10] = (byte)(intValue);

                // Position Auflösung
                intValue = 3456;
                _sendData[11] = (byte)(intValue >> 8);
                _sendData[12] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x27)
            {   // CCC nav gps date/time
                Array.Copy(_responseA022F127, _sendData, _responseA022F127.Length);

                if (VariableValues)
                {
                    DateTime dateTime = DateTime.Now.ToUniversalTime();
                    // year (bcd), real coding seems to be different
                    _sendData[7] = IntToBcd(dateTime.Year / 100);
                    _sendData[8] = IntToBcd(dateTime.Year % 100);    // real code is 0xA4

                    _sendData[9] = IntToBcd(dateTime.Month);    // month
                    _sendData[10] = IntToBcd(dateTime.Day);     // day
                    // time (bcd)
                    _sendData[11] = IntToBcd(dateTime.Hour);    // hour
                    _sendData[12] = IntToBcd(dateTime.Minute);  // min
                    _sendData[13] = IntToBcd(dateTime.Second);  // sec
                }

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0xA0 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0xF1 &&
                _receiveData[5] == 0x28)
            {   // CCC nav gps satellites
                Array.Copy(_responseA022F128, _sendData, _responseA022F128.Length);

                _sendData[8] = 20;   // verfolgbare satelliten
                _sendData[9] = 22;   // empfangbare satelliten

                ObdSend(_sendData);
            }
            // device 0x040
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x40 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x90)
            {
                Array.Copy(_response401A90, _sendData, _response401A90.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0x40 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {
                Array.Copy(_response401802FFFF, _sendData, _response401802FFFF.Length);

                _sendData[0] = 0x82;
                _sendData[4] = 0x00;

                ObdSend(_sendData);
            }
            // device 0x60
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x60 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {
                Array.Copy(_response601A80, _sendData, _response601A80.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x60 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0x0B)
            {
                Array.Copy(_response60210B, _sendData, _response60210B.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x60 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0x17)
            {
                Array.Copy(_response602117, _sendData, _response602117.Length);
                ObdSend(_sendData);
            }
            // device 0x70
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x70 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x22 &&
                _receiveData[4] == 0x10 &&
                _receiveData[5] == 0x00)
            {
                Array.Copy(_response70221000, _sendData, _response70221000.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x70 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {
                Array.Copy(_response701A80, _sendData, _response701A80.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x70 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x90)
            {
                Array.Copy(_response701A90, _sendData, _response701A90.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x86 &&
                _receiveData[1] == 0x70 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x23 &&
                _receiveData[4] == 0x00 &&
                _receiveData[5] == 0x00 &&
                _receiveData[6] == 0x00 &&
                _receiveData[7] == 0x07 &&
                _receiveData[8] == 0x12)
            {
                Array.Copy(_response70230000000712, _sendData, _response70230000000712.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x86 &&
                _receiveData[1] == 0x70 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x23 &&
                _receiveData[4] == 0x00 &&
                _receiveData[5] == 0x00 &&
                _receiveData[6] == 0x12 &&
                _receiveData[7] == 0x07 &&
                _receiveData[8] == 0x12)
            {
                Array.Copy(_response70230000120712, _sendData, _response70230000120712.Length);
                ObdSend(_sendData);
            }
            // device 0x73 CID
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x73 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {
                Array.Copy(_response731A80, _sendData, _response731A80.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0x73 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {
                Array.Copy(_response731802FFFF, _sendData, _response731802FFFF.Length);
                ObdSend(_sendData);
            }
            // device 0x78 IHK2
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x78 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {
                Array.Copy(_response781A80, _sendData, _response781A80.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0x78 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {
                Array.Copy(_response781802FFFF, _sendData, _response781802FFFF.Length);
                ObdSend(_sendData);
            }
            // device 0x64 PDC
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x64 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {
                Array.Copy(_response641A80, _sendData, _response641A80.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0x64 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {
                Array.Copy(_response641802FFFF, _sendData, _response641802FFFF.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x64 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x17 &&
                _receiveData[4] == 0xE2 &&
                _receiveData[5] == 0x05)
            {
                Array.Copy(_response6417E205, _sendData, _response6417E205.Length);

                // Fehlerhäufigkeit
                _sendData[8] = 5;

                // Kilometerstand
                // 0xFFFF: 524280 = 0x7FFF8 (Left shift 3)
                int intValue = (int)(123456 >> 3);
                _sendData[9] = (byte)(intValue >> 8);
                _sendData[10] = (byte)(intValue);

                // 0x0000 == -50.1
                // 0x8000 == 499.9
                // (temp [C] + 50.1) * 0x8000 / 550.0

                // Temperatur
                // 0x00: -40.00
                // 0xFF: 87.5
                // (temp [C] + 40.00) * 0xFF / 127.5
                intValue = (int)((30.0 + 40.00) * 0xFF / 127.5);
                _sendData[11] = (byte)intValue;

                // Kilometerstand
                // 0xFFFF: 524280 = 0x7FFF8 (Left shift 3)
                intValue = (int)(234567 >> 3);
                _sendData[12] = (byte)(intValue >> 8);
                _sendData[13] = (byte)(intValue);

                // Temperatur
                // 0x00: -40.00
                // 0xFF: 87.5
                // (temp [C] + 40.00) * 0xFF / 127.5
                intValue = (int)((80.0 + 40.00) * 0xFF / 127.5);
                _sendData[14] = (byte)intValue;

                ObdSend(_sendData);
            }
            // device 0x65 SZM
            else if (
                _receiveData[0] == 0x82 &&
                _receiveData[1] == 0x65 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x1A &&
                _receiveData[4] == 0x80)
            {
                Array.Copy(_response651A80, _sendData, _response651A80.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x84 &&
                _receiveData[1] == 0x65 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x18 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0xFF &&
                _receiveData[6] == 0xFF)
            {
                Array.Copy(_response651802FFFF, _sendData, _response651802FFFF.Length);
                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x65 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x17 &&
                _receiveData[4] == 0x9F &&
                _receiveData[5] == 0xF1)
            {
                Array.Copy(_response65179FF1, _sendData, _response65179FF1.Length);

                // Fehlerart
                // Bit 0-3:
                // 0: Kein passendes Fehlersymptom
                // 1: Signal oder Wert oberhalb der Schwelle
                // 2: Signal oder Wert unterhalb der Schwelle
                // 3: Unbekannte Fehlerart
                // 4: Kein Signal oder Wert
                // 5-7: Unbekannte Fehlerart
                // 8: Unplausibles Signal oder Wert
                // 9-15: Unbekannte Fehlerart
                //
                // Bit 4: 1=Testbedingung noch nicht erfüllt
                //
                // Bit 5-6:
                // 0: Fehler bisher nicht aufgetreten
                // 1: Fehler momentan nicht vorhanden, aber bereits gespeichert
                // 2: Fehler momentan vorhanden, aber noch nicht gespeichert
                // 3: Fehler momentan vorhanden, und bereits gespeichert
                //
                // Bit 7: 1=Fehler würde das Aufleuchten einer Warnlampe verursachen
                _sendData[7] = 0xA4;

                // Kilometerstand
                // 0xFFFF: 524280 = 0x7FFF8 (Left shift 3)
                int intValue = (int)(123456 >> 3);
                _sendData[8] = (byte)(intValue >> 8);
                _sendData[9] = (byte)(intValue);

                intValue = (int)(234567 >> 3);
                _sendData[10] = (byte)(intValue >> 8);
                _sendData[11] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x65 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x21 &&
                _receiveData[4] == 0xF9 &&
                _receiveData[5] == 0x07)
            {   // Key status
                Array.Copy(_response6521F907, _sendData, _response6521F907.Length);

                // Bit 2: PDC
                // Bit 4: FDC
                _sendData[6] = 0x04;

                // Bit 0: Heizung links
                // Bit 1: Klima links
                // Bit 2: Aktivsitz links
                // Bit 4: Heizung rechts
                // Bit 5: Klima rechts
                // Bit 6: Aktivsitz rechts
                _sendData[7] = 0x00;

                ObdSend(_sendData);
            }
            // device 0x78 IHK
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x78 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x30 &&
                _receiveData[4] == 0x02 &&
                _receiveData[5] == 0x01)
            {   // Status Regler
                Array.Copy(_response78300201, _sendData, _response78300201.Length);

                // Sollwert Basis
                // 0x00: 0 °C
                // 0xFF: 127.5 °C
                // (temp [C]) * 0xFF / 127.5
                int intValue = (int)(15.0 * 0xFF / 127.5);
                _sendData[7] = (byte)(intValue);

                // Aussenwert
                // 0x00: -40.00
                // 0xFF: 87.5
                // (temp [C] + 40.00) * 0xFF / 127.5
                intValue = (int)((35.0 + 40.00) * 0xFF / 127.5);
                _sendData[8] = (byte)(intValue);

                // Waermetauscher Istwert (rechts)
                // 0x00: 5°
                // 0xFF: 132.5°
                // (Istwert [C] - 5) * 0xFF / 127.5
                intValue = (int)((90.0 - 5) * 0xFF / 127.5);
                _sendData[10] = (byte)(intValue);

                // Innenwert
                // 0x00: 10.0
                // 0xFF: 52.5
                // (temp [C] - 10.0) * 0xFF / 42.5
                intValue = (int)((20.0 - 10.0) * 0xFF / 42.5);
                _sendData[11] = (byte)(intValue);

                // Luftleistung
                // 0x00: 0%
                // 0xFF: 255%
                intValue = 45;
                _sendData[12] = (byte)(intValue);

                // Hauptstellgroesse (rechts)
                // 0x00: -27%
                // 0xFF: 100%
                // (Stell [%] + 27.0) * 0xFF / 127
                intValue = (int)((90.0 + 27.0) * 0xFF / 127);
                _sendData[14] = (byte)(intValue);

                // Wasserventiloeffnungszeit (rechts)
                // 0x0000: 0 ms
                // 0xFFFF: -1 ms
                intValue = 1234;
                _sendData[17] = (byte)(intValue >> 8);
                _sendData[18] = (byte)(intValue);

                // Innenwert verzoegert
                // 0x00: 10.0
                // 0xFF: 52.5
                // (temp [C] - 10.0) * 0xFF / 42.5
                intValue = (int)((22.0 - 10.0) * 0xFF / 42.5);
                _sendData[19] = (byte)(intValue);

                // Sollwert (links)
                // 0x00: 10°
                // 0xFF: 52.5°
                // (temp [C] - 10.0) * 0xFF / 42.5
                intValue = (int)((30.0 - 10.0) * 0xFF / 42.5);
                _sendData[20] = (byte)(intValue);

                // Waermetauschersollwert (rechts)
                // 0x00: 5°
                // 0xFF: 132.5°
                // (Sollwert [C] - 5) * 0xFF / 127.5
                intValue = (int)((100.0 - 5) * 0xFF / 127.5);
                _sendData[23] = (byte)(intValue);

                // Waermetauscherstellgroesse (rechts)
                // 0x00: 0%
                // 0xFF: 127%
                // (Stell [%]) * 0xFF / 127
                intValue = (int)((50.0) * 0xFF / 127);
                _sendData[25] = (byte)(intValue);

                // Führungsgroesse (links)
                // 0x0000: 0%
                // 0xFFFF: -1%
                intValue = 30;
                _sendData[28] = (byte)(intValue >> 8);
                _sendData[29] = (byte)(intValue);

                // Geschwindigkeit
                // 0x00: 0 km/h
                // 0xFF: 255 km/h
                intValue = 150;
                _sendData[32] = (byte)(intValue);

                // Motordrehzahl
                // 0x00: 0 1/min
                // 0xFF: 12750 1/min
                intValue = (int)((1000.0) * 0xFF / 12750);
                _sendData[33] = (byte)(intValue);

                ObdSend(_sendData);
            }
            else if (
                _receiveData[0] == 0x83 &&
                _receiveData[1] == 0x78 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x30 &&
                _receiveData[4] == 0x06 &&
                _receiveData[5] == 0x01)
            {   // Status Digital
                Array.Copy(_response78300601, _sendData, _response78300601.Length);

                ObdSend(_sendData);
            }
            else
            {   // nothing matched, check response list
                return false;
            }
            return true;
        }

        private bool ResponseE90()
        {
            if (
                (_receiveData[0] & 0xC0) == 0x80 &&
                _receiveData[1] == 0x12 &&
                _receiveData[2] == 0xF1 &&
                _receiveData[3] == 0x2C &&
                _receiveData[4] == 0x10)
            {   // request list
                int i = 0;
                _sendData[i++] = 0x82;
                _sendData[i++] = 0xF1;
                _sendData[i++] = 0x12;
                _sendData[i++] = 0x6C;
                _sendData[i++] = 0x10;

                int items = ((_receiveData[0] & 0x3F) - 2) / 2;
                if (items == 0)
                {   // use last request data
                    if (_receiveDataMotorBackup[1] == 0x12)
                    {
                        _receiveDataMotorBackup.CopyTo(_receiveData, 0);
                        items = ((_receiveData[0] & 0x3F) - 2) / 2;
                    }
                }
                else
                {
                    _receiveData.CopyTo(_receiveDataMotorBackup, 0);
                }
                for (int j = 0; j < items; j++)
                {
                    int itemAddr = ((int)_receiveData[5 + j * 2] << 8) + _receiveData[6 + j * 2];
                    long itemValue = 0x000;
                    int resultBytes = 2;
                    switch (itemAddr)
                    {
                        case 0x0005:    // refrigerant temp (sensor)
                            // temp [C] * 1.000000 -40.000000
                            itemValue = (long)(50.0 + 40.000000);
                            resultBytes = 1;
                            break;

                        case 0x0010:    // Luftmasse von HFM (OBD_PID10_AFS_dmSens)
                            // (air * 0.010000);
                            itemValue = (long)(355.0 / 0.010000);
                            break;

                        case 0x0042:    // battery voltage
                            // bat [V] * 0.001000
                            itemValue = (long)(_batteryVoltage / 100.0 / 0.001000);
                            break;

                        case 0x012C: // Batteriespannung korrigiert
                            // bat [mV] * 0.389105
                            itemValue = (long)(_batteryVoltage * 10.0 / 0.389105);
                            break;

                        case 0x01F4:    // Ladedruck soll
                            // Druck [mbar] * 0.091554
                            itemValue = (long)(1938.0 / 0.091554);
                            break;

                        case 0x0385:    // Kraftstofftemperatur
                            // (temp [C] * 0.010000) -50.000000
                            itemValue = (long)((40.3 + 50.000000) / 0.010000);
                            break;

                        case 0x03EB:    // Partikelfilter Strecke seit Regeneration (IDSLRE)
                            // Strecke [m]
                            itemValue = 145678;
                            resultBytes = 4;
                            break;

                        case 0x03EE:    // Partikelfilter Freigabe Regeneration (ISRBF)
                            // 0 == freigegeben
                            // 1 == gesperrt
                            itemValue = 0;
                            resultBytes = 1;
                            break;

                        case 0x0404:    // Partikelfilter Anforderung Regeneration (PFltRgn_numRgn)
                            // 4 - 6 == angefordert
                            // other: nicht angefordert
                            itemValue = 4;
                            resultBytes = 1;
                            break;

                        case 0x041B:    // Abgastemp vor Partikelfilter
                            // (temp [C] * 0.031281) -50.000000
                            itemValue = (long)((165.3 + 50.000000) / 0.031281);
                            break;

                        case 0x041E:    // Abgastemp vor Kat
                            // (temp [C] * 0.031281) -50.000000
                            itemValue = (long)((175.3 + 50.000000) / 0.031281);
                            break;

                        case 0x0424:    // Abgasgegendruck
                            // Druck [mbar]
                            itemValue = (long)(2943);
                            break;

                        case 0x0458:    // oil temp
                            // temp [C] * 0.010000 -100.000000
                            itemValue = (long)((60.0 + 100.000000) / 0.010000);
                            break;

                        case 0x0547:    // refrigerant temp
                            // temp [C] * 0.010000 -100.000000
                            itemValue = (long)((50.0 + 100.000000) / 0.010000);
                            break;

                        case 0x05AA:    // Partikelfilter Status Regeneration (CoEOM_stOpModeAct)
                            // bit1 set == aktiv
                            // other == inaktiv
                            itemValue = 0x02;
                            resultBytes = 4;
                            break;

                        case 0x0641:    // Raildruck soll
                            // Druck [mbar] * 0.045777
                            itemValue = (long)(1024.0 / 0.045777);
                            break;

                        case 0x0672:    // Raildruck ist
                            // Druck [mbar] * 0.045777
                            itemValue = (long)(1027.0 / 0.045777);
                            break;

                        case 0x0708:    // Luftmasse (ILMKG)
                            // (air * 0.100000);
                            itemValue = (long)(350.0 / 0.100000);
                            break;

                        case 0x0709:    // Luftmasse ist
                            // (lm [mg] * 0.024414)
                            itemValue = (long)(527.0 / 0.024414 );
                            break;

                        case 0x076D:    // Ladedruck ist
                            // Druck [mbar] * 0.091554
                            itemValue = (long)(1935.0 / 0.091554);
                            break;

                        case 0x076F:    // Ladelufttemp
                            // (temp [C] * 0.010000) -100.000000
                            itemValue = (long)((60.1 + 100.000000) / 0.010000);
                            break;

                        case 0x0772:    // intake air temp
                            // (temp [C] * 0.100000) -273.140000
                            itemValue = (long)((80.0 + 273.140000) / 0.100000);
                            break;

                        case 0x079E:    // Luftmasse soll
                            // (lm [mg] * 0.030518)
                            itemValue = (long)(523.0 / 0.030518);
                            break;

                        case 0x0ABE:    // Öldruckschalter
                            itemValue = 0x0001;
                            break;

                        case 0x0AF1:    // motor temp
                            // temp [C] * 0.100000 -273.140000
                            itemValue = (long)((50.0 + 273.140000) / 0.100000);
                            break;

                        case 0x0BA4:    // Partikelfilter Restlaufstrecke
                            // dist * 10
                            itemValue = (long)(100000d / 10);
                            break;

                        case 0x0C1C:    // Umgebungsdruck
                            // Druck [mbar] * 0.030518
                            itemValue = (long)(935.0 / 0.030518);
                            break;

                        case 0x0FD2:    // Umgebungstemperatur
                            // (temp [C] * 0.100000) -273.140000
                            itemValue = (long)((35.4 + 273.140000) / 0.100000);
                            break;

                        case 0x1881:    // motor rpm
                            // rpm * 0.500000
                            itemValue = (long)(400.0 / 0.500000);
                            break;
                    }
                    if (resultBytes >= 4) _sendData[i++] = (byte)(itemValue >> 24);
                    if (resultBytes >= 3) _sendData[i++] = (byte)(itemValue >> 16);
                    if (resultBytes >= 2) _sendData[i++] = (byte)(itemValue >> 8);
                    if (resultBytes >= 1) _sendData[i++] = (byte)(itemValue);
                }
                _sendData[0] = (byte)(0x80 | (i - 3));

                ObdSend(_sendData);
            }
            else
            {   // nothing matched, check response list
                return false;
            }
            return true;
        }

        private void SerialConcept1Transmission()
        {
            int recLength = 0;
            for (;;)
            {
                if (!ReceiveData(_receiveData, recLength, 1))
                {   // complete tel received
                    break;
                }
                recLength++;
            }
            if (recLength == 0)
            {
                return;
            }
            if (!_adsAdapter && !_klineResponder)
            {
                // send echo
                SendData(_receiveData, recLength);
            }

            bool found = false;
            foreach (ResponseEntry responseEntry in _configData.ResponseList)
            {
                if (recLength != responseEntry.Request.Length) continue;
                bool equal = true;
                for (int i = 0; i < recLength - 1; i++)
                {   // don't compare checksum
                    if (_receiveData[i] != responseEntry.Request[i])
                    {
                        equal = false;
                        break;
                    }
                }
                if (equal)
                {       // entry found
                    found = true;
                    byte[] response = responseEntry.ResponseList[0];
                    int responseLen = response.Length;
                    if (responseLen > 0)
                    {
                        Array.Copy(response, _sendData, responseLen);
                        _sendData[responseLen - 1] = CalcChecksumXor(_sendData, responseLen - 1);
                        SendData(_sendData, responseLen);
                    }
                    break;
                }
            }
            if (!found)
            {
                string text = string.Empty;
                for (int i = 0; i < recLength; i++)
                {
                    text += string.Format("{0:X02} ", _receiveData[i]);
                }
                Debug.WriteLine("Not found: " + text);
            }
        }

        private void SerialIso9141Transmission()
        {
            bool initOk;
            do
            {
                initOk = false;
                byte wakeAddress = 0x00;
                if (!ReceiveWakeUp(out wakeAddress))
                {
                    break;
                }
                Debug.WriteLine("Wake Address: {0:X02}", wakeAddress);
                if ((_configData.ConfigList.Count > 1) && (wakeAddress != _configData.ConfigList[0]))
                {
                    Debug.WriteLine("Invalid wake address");
                    continue;
                }

                Thread.Sleep(100);  // maximum is 2000ms
                _sendData[0] = 0x55;
                SendData(_sendData, 0, 1);

                Thread.Sleep(100);   // maximum 400ms
                int sendLen;
                if (_configData.ConfigList.Count > 1)
                {
                    byte[] configArray = _configData.ConfigList.ToArray();
                    sendLen = configArray.Length - 1;
                    Array.Copy(configArray, 1, _sendData, 0, sendLen);
                }
                else
                {
                    sendLen = 2;
                    _sendData[0] = 0x08;
                    _sendData[1] = 0x08;
                }

                SendData(_sendData, 0, sendLen);

                //if (ReceiveData(_receiveData, 0, 1, 50, 50))  // too fast for ELM
                if (ReceiveData(_receiveData, 0, 1, 70, 70))
                {
                    if ((byte) (~_receiveData[0]) == _sendData[1])
                    {
                        initOk = true;
                    }
                    else
                    {
                        Debug.WriteLine("Invalid init response {0}", (byte)(~_receiveData[0]));
                    }
                }
                else
                {
                    Debug.WriteLine("No init response");
                }
            } while (!initOk);

            Debug.WriteLine("Init done");

            byte blockCount = 1;
            int telBlockIndex = 0;
            int initSequenceCount = 0;
            ResponseEntry activeResponse = null;
            for (; ; )
            {
                if (_stopThread)
                {
                    break;
                }

                if (initSequenceCount < _configData.ResponseOnlyList.Count)
                {
                    byte[] responseOnly = _configData.ResponseOnlyList[initSequenceCount];
                    Array.Copy(responseOnly, _sendData, responseOnly.Length);
                    initSequenceCount++;
                }
                else
                {
                    _sendData[0] = 0x03;    // block length
                    _sendData[2] = 0x09;    // ACK
                }

                if (activeResponse != null)
                {
                    if (telBlockIndex < activeResponse.ResponseMultiList.Count)
                    {
                        byte[] responseTel = activeResponse.ResponseMultiList[telBlockIndex];
                        Array.Copy(responseTel, _sendData, responseTel.Length);
                        telBlockIndex++;
                    }
                    if (telBlockIndex >= activeResponse.ResponseMultiList.Count)
                    {
                        activeResponse = null;
                    }
                }
                _sendData[1] = blockCount++;    // block counter

                if (!SendIso9141Block(_sendData))
                {
                    Debug.WriteLine("Send block failed");
                    break;
                }

                if (!ReceiveIso9141Block(_receiveData))
                {
                    Debug.WriteLine("Receive block failed");
                    break;
                }
                if (blockCount != _receiveData[1])
                {
                    Debug.WriteLine("Block count invalid");
                    //break;
                }
                blockCount++;
                byte command = _receiveData[2];
                int recLength = _receiveData[0];
                if (command != 0x09)
                {   // no ack
                    bool found = false;
                    foreach (ResponseEntry responseEntry in _configData.ResponseList)
                    {
                        if (recLength != responseEntry.Request.Length) continue;
                        bool equal = true;
                        for (int i = 0; i < recLength; i++)
                        {
                            if (i == 1)
                            {   // don't compare block count
                                continue;
                            }
                            if (_receiveData[i] != responseEntry.Request[i])
                            {
                                equal = false;
                                break;
                            }
                        }
                        if (equal)
                        {       // entry found
                            found = true;
                            activeResponse = responseEntry;
                            telBlockIndex = 0;
                            break;
                        }
                    }
                    if (!found)
                    {
                        string text = string.Empty;
                        for (int i = 0; i < recLength; i++)
                        {
                            text += string.Format("{0:X02} ", _receiveData[i]);
                        }
                        Debug.WriteLine("Not found: " + text);
                    }
                }
            }
        }

        private void SerialConcept3Transmission()
        {
            bool initOk;
            do
            {
                initOk = false;
                _serialPort.DataBits = 8;
                _serialPort.Parity = Parity.None;
                byte wakeAddress = 0x00;
                if (!ReceiveWakeUp(out wakeAddress))
                {
                    break;
                }
                Debug.WriteLine("Wake Address: {0:X02}", wakeAddress);
                if ((_configData.ConfigList.Count > 1) && (wakeAddress != _configData.ConfigList[0]))
                {
                    Debug.WriteLine("Invalid wake address");
                    continue;
                }

                Thread.Sleep(100);  // maximum is 2200ms
                _sendData[0] = 0x55;
                SendData(_sendData, 0, 1);

                Thread.Sleep(10);   // maximum 200ms
                int sendLen = 0;
                if (_configData.ConfigList.Count > 1)
                {
                    byte[] configArray = _configData.ConfigList.ToArray();
                    sendLen = configArray.Length - 1;
                    Array.Copy(configArray, 1, _sendData, 0, sendLen);
                }

                if (sendLen > 1)
                {
                    SendData(_sendData, 1, sendLen);
                    Thread.Sleep(10);
                    _serialPort.DataBits = 8;
                    _serialPort.Parity = Parity.Even;
                    //Thread.Sleep(10);     // max sum of both timeouts 2500ms
                    Thread.Sleep(200);
                }
                initOk = true;
            } while (!initOk);

            Debug.WriteLine("Init done");

            bool stopSend = false;
            for (;;)
            {
                if (stopSend)
                {
                    break;
                }

                foreach (byte[] responseOnly in _configData.ResponseOnlyList)
                {
                    if (_stopThread)
                    {
                        stopSend = true;
                        break;
                    }
                    if (_serialPort.BytesToRead > 0)
                    {
                        Debug.WriteLine("Abort comm");
                        Thread.Sleep(100);
                        stopSend = true;
                        break;
                    }
                    int responseLen = responseOnly.Length;
                    if (responseLen > 0)
                    {
                        Array.Copy(responseOnly, _sendData, responseLen);
                        _sendData[responseLen - 1] = CalcChecksumXor(_sendData, responseLen - 1);
                        SendData(_sendData, responseLen);
                        // max interbyte timeout is 10ms
                        Thread.Sleep(200);  // min 150ms, max 2500ms (this time includes the send time of 50ms!)
                    }
                }
            }
        }

    }
}
