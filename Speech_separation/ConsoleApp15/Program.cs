using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using ClosedXML.Excel;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using Google.Apis.Auth.OAuth2;
using Grpc.Core;
using Grpc.Auth;
using Google.Cloud.Speech.V1;
using System.Timers;

//計算量を抑えるためにILD256/IPD256を利用する。(10度ごとのデータ)
//フレームごとの分離可能（ノイズ有　スケールの調整方法について考慮していく必要有）

namespace ConsoleApp15
{
    class Wave_file_cr
    {
        short headerSize = 44;
        short channels = 1;
        short bitsPerSample = 16;
        int sampleRate = 16000;
        int datasize;

        public byte[] _wav { get; set; }
        byte[] stock_data;
        List<byte> data;

        short _seq = -1;
        int delay_seq = 10;

        public List<byte> File_data_list = new List<byte>();
        public byte[] File_data { get; set; }
        /// <summary>
        /// The reason to convert short type to double type is
        ///         to prepare for appropriate process in Seg
        /// </summary>
        public double[] File_Data_L { get; private set; }
        public double[] File_Data_R { get; private set; }


        WaveHeader _waveheader;

        struct WaveHeader
        {
            public string Rifft_Header;
            public int File_Size;
            public string Wave_Header;
            public string Format_Chunk;
            public int Format_Chunk_Size;
            public short Format_ID;
            public short Channel;
            public int Sample_Rate;
            public int Byte_Per_Sec;
            public short BlockSize;
            public short Bit_Per_Sample;
            public string Data_Chunk;
            public int Data_Chunk_Size;

        }

        public Wave_file_cr()
        {
            _wav = new byte[headerSize];
            data = new List<byte>();
            stock_data = new byte[200];
            _waveheader = new WaveHeader();
        }

        void Wave_header(int sampledatabytesize)
        {
            datasize = sampledatabytesize;
            channels = 1;

            Console.WriteLine("header");
            byte[] riff = Encoding.UTF8.GetBytes("RIFF");
            Array.Copy(riff, 0, _wav, 0, riff.Length);
            byte[] chunk = BitConverter.GetBytes(headerSize + datasize - 8);
            Array.Copy(chunk, 0, _wav, 4, chunk.Length);
            byte[] _wave = Encoding.UTF8.GetBytes("WAVE");
            Array.Copy(_wave, 0, _wav, 8, _wave.Length);
            byte[] fmt = Encoding.UTF8.GetBytes("fmt ");
            Array.Copy(fmt, 0, _wav, 12, fmt.Length);
            byte[] _rate = BitConverter.GetBytes(16);
            Array.Copy(_rate, 0, _wav, 16, _rate.Length);
            byte[] format = BitConverter.GetBytes((short)1);
            Array.Copy(format, 0, _wav, 20, format.Length);
            byte[] _channels = BitConverter.GetBytes(channels);
            Array.Copy(_channels, 0, _wav, 22, _channels.Length);
            byte[] _samplerate = BitConverter.GetBytes(sampleRate);
            Array.Copy(_samplerate, 0, _wav, 24, _samplerate.Length);
            byte[] b_rate = BitConverter.GetBytes(sampleRate * (short)(bitsPerSample / 8) * channels);
            Array.Copy(b_rate, 0, _wav, 28, b_rate.Length);
            byte[] block = BitConverter.GetBytes((short)(bitsPerSample / 8) * channels);
            Array.Copy(block, 0, _wav, 32, block.Length);
            byte[] b_p_s = BitConverter.GetBytes(bitsPerSample);
            Array.Copy(b_p_s, 0, _wav, 34, b_p_s.Length);
            byte[] d = Encoding.UTF8.GetBytes("data");
            Array.Copy(d, 0, _wav, 36, d.Length);
            byte[] d_s = BitConverter.GetBytes(datasize);
            Array.Copy(d_s, 0, _wav, 40, d_s.Length);
        }

        public void Wave_File_Binary(string filename)
        {
            using (FileStream fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var br = new BinaryReader(fs);
                _waveheader.Rifft_Header = Encoding.GetEncoding(20127).GetString(br.ReadBytes(4));
                _waveheader.File_Size = BitConverter.ToInt32(br.ReadBytes(4), 0);
                _waveheader.Wave_Header = Encoding.GetEncoding(20127).GetString(br.ReadBytes(4));

                var readFmtChunk = false;
                var readDataChunk = false;
                while (readFmtChunk == false || readDataChunk == false)
                {
                    var chunk = System.Text.Encoding.GetEncoding(20127).GetString(br.ReadBytes(4));
                    if (chunk.ToLower().CompareTo("fmt ") == 0)
                    {
                        _waveheader.Format_Chunk = chunk;
                        _waveheader.Format_Chunk_Size = BitConverter.ToInt32(br.ReadBytes(4), 0);
                        _waveheader.Format_ID = BitConverter.ToInt16(br.ReadBytes(2), 0);
                        _waveheader.Channel = BitConverter.ToInt16(br.ReadBytes(2), 0);
                        _waveheader.Sample_Rate = BitConverter.ToInt32(br.ReadBytes(4), 0);
                        _waveheader.Byte_Per_Sec = BitConverter.ToInt32(br.ReadBytes(4), 0);
                        _waveheader.BlockSize = BitConverter.ToInt16(br.ReadBytes(2), 0);
                        _waveheader.Bit_Per_Sample = BitConverter.ToInt16(br.ReadBytes(2), 0);
                        readFmtChunk = true;
                    }
                    else if (chunk.ToLower().CompareTo("data") == 0)
                    {
                        _waveheader.Data_Chunk = chunk;
                        _waveheader.Data_Chunk_Size = BitConverter.ToInt32(br.ReadBytes(4), 0);
                        byte[] b = br.ReadBytes(_waveheader.Data_Chunk_Size);

                        File_Data_L = new double[_waveheader.Data_Chunk_Size / 4];
                        File_Data_R = new double[_waveheader.Data_Chunk_Size / 4];
                        Console.WriteLine("Start");
                        int i = 0;
                        for (int j = 0; j < b.Length - 4; j += 4)
                        {
                            File_Data_L[i] = BitConverter.ToInt16(b, j) / (double)short.MaxValue;
                            File_Data_R[i] = BitConverter.ToInt16(b, j + 2) / (double)short.MaxValue;
                            i++;
                        }
                        return;
                    }
                }
            }
        }

        public void Wave_data(byte[] _data)
        {
            lock (this)
            {
                short seq = BitConverter.ToInt16(new byte[] { _data[0], _data[1] }, 0);
                if (_seq - delay_seq < seq)
                {
                    Array.Copy(_data, 2, stock_data, 0, _data.Length - 2);
                    data.AddRange(new ArraySegment<byte>(_data, 2, _data.Length - 2));
                    _seq = seq;
                }
                else
                {
                    data.AddRange(stock_data);
                }
            }
        }

        public void Udp_Progress_D()
        {
            byte[] b = data.ToArray();
            File_Data_L = new double[b.Length / 4];
            File_Data_R = new double[b.Length / 4];
            Console.WriteLine("Start");
            int i = 0;
            for (int j = 0; j <= (b.Length - 4); j += 4)
            {
                File_Data_L[i] = BitConverter.ToInt16(b, j);
                File_Data_R[i] = BitConverter.ToInt16(b, j + 2);
                i++;
            }
        }

        public void Write_wav_f(int name)
        {
            string file = string.Format(@".\Test\text{0}.wav", name);
            Wave_header(File_data.Length);
            using (FileStream sw = new FileStream(file, FileMode.Create, FileAccess.Write))
            {
                sw.Write(_wav, 0, _wav.Length);
                Console.WriteLine("data");
                sw.Write(File_data, 0, File_data.Length);
                Console.WriteLine(File_data.Length);
                Console.WriteLine("finish!!");
                //sw.Close();
            }
        }

    }
    /// <summary>
    /// It get row data or column data from two dimensional array
    /// </summary>
    class Two_dim
    {
        double[,] _data;

        public Two_dim(double[,] _data)
        {
            this._data = _data;
        }

        public double[] Get_Array(string Type, int x, int y)
        {
            double[] Array_Data;

            if (Type == "row")
            {
                Array_Data = new double[y];
                for (int i = 0; i < y; i++)
                {
                    Array_Data[i] = _data[i, x];
                }

            }
            else if (Type == "column")
            {
                Array_Data = new double[x];
                for (int i = 0; i < x; i++)
                {
                    Array_Data[i] = _data[y, i];
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(Type));
            }
            return Array_Data;
        }

    }

    interface IData_Type
    {
        double[,] Read_Data();
        double[] DataRange();
    }

    class Get_IPD : IData_Type
    {
        double[,] IPD;
        double[] Prange;
        double[,] normparameterbuf;

        public Get_IPD()
        {
            IPD = new double[19, 25];
            normparameterbuf = new double[19, 25];
            Prange = new double[256];
        }

        public double[,] Read_Data()
        {
            XLWorkbook workbook1 = new XLWorkbook(@".\IPD256.xlsx");
            IXLWorksheet worksheet1 = workbook1.Worksheet(1);
            IXLCell cell;
            int lastRow1 = worksheet1.LastRowUsed().RowNumber();
            for (int i = 0; i < 19; i++)
            {
                for (int j = 0; j < 25; j++)
                {
                    cell = worksheet1.Cell(i + 1, j + 1);
                    try
                    {
                        IPD[i, j] = Convert.ToDouble(cell.Value);
                    }
                    catch
                    {
                        IPD[i, j] = 0;
                    }
                }
            }
            return IPD;
        }

        public double[] DataRange()
        {
            Two_dim two_dim = new Two_dim(IPD);
            double[] buf;
            for (int i = 0; i < 25; i++)
            {
                buf = two_dim.Get_Array("row", i, 19);
                Prange[i] = buf.Max() - buf.Min();
            }
            return Prange;
        }
    }

    class Get_ILD : IData_Type
    {
        double[,] ILD;
        double[,] _ILD;
        double[] Lrange;
        double[,] normparameterbuf;

        public Get_ILD()
        {
            ILD = new double[19, 256];
            _ILD = new double[19, 256];
            normparameterbuf = new double[19, 256];
            Lrange = new double[256];
        }

        public double[,] Read_Data()
        {
            XLWorkbook workbook = new XLWorkbook(@".\ILD256.xlsx");
            IXLWorksheet worksheet = workbook.Worksheet(1);
            IXLCell cell;
            int lastRow = worksheet.LastRowUsed().RowNumber();
            for (int i = 0; i < 19; i++)
            {
                for (int j = 0; j < 256; j++)
                {
                    cell = worksheet.Cell(i + 1, j + 1);
                    try
                    {
                        ILD[i, j] = Convert.ToDouble(cell.Value);
                    }
                    catch
                    {
                        ILD[i, j] = 0;
                    }
                }
            }
            return ILD;
        }
        public double[] DataRange()
        {
            Two_dim two_dim = new Two_dim(ILD);
            double[] buf;
            for (int i = 25; i < 256; i++)
            {
                buf = two_dim.Get_Array("row", i, 19);
                Lrange[i] = buf.Max() - buf.Min();
            }
            return Lrange;
        }
    }

    /// <summary>
    /// This function read the fdbm data
    /// </summary>
    class CreateReadData
    {

        public CreateReadData() { }

        public void Process(string Type, int border_n, out double[,] _data, out double[] _datarange)
        {
            var r_data = CreateData(Type);
            _data = r_data.Read_Data();
            _datarange = r_data.DataRange();
        }

        private IData_Type CreateData(string Type)
        {
            if (Type == "ILD")
            {
                return new Get_ILD();
            }
            else if (Type == "IPD")
            {
                return new Get_IPD();
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(Type));
            }
        }
    }


    class Seg
    {
        int frame_length = 512;
        int frame_length_half = 512 >> 1;
        int adelay = 512; //less than frame_length
        int frame_shift = 16;
        double forgetting = 0.9; // previous gain is used as forgetting rate.thus new gain is (1 - forgetting)
        int binarymask = 0;
        public int[] Ar_target_beam { set; get; }
        double filternoise = 0.001;
        int fs = 16000;
        double border_frequency = 125 * 6;

        double forgetting2;
        double border_n;
        double numofloop;

        double[] input_win;
        double[] normDbuf;
        double[,] IPD;
        double[] P_range;
        double[,] ILD;
        double[] L_range;

        double sigmax;

        double[] normparameterbuf;
        double[] normD6;
        double[] normD62;
        public double[] _output { get; set; }
        public double[] output_l { get; set; }
        public double[] output_r { get; set; }

        public double[] input_l_list { get; set; }
        public double[] input_r_list { get; set; }
        public double[] output_l_list { get; set; }
        public double[] output_r_list { get; set; }

        public Seg()
        {
            Ar_target_beam = new int[10];
            input_win = new double[frame_length];
            forgetting2 = forgetting / (frame_length / frame_shift);
            border_n = border_frequency / (fs / frame_length);
            normparameterbuf = new double[256];
            normD6 = new double[256];
            normD62 = new double[512];
        }

        public void Seg_initial_set_param(int len_l, int len_r)
        {
            numofloop = (len_l - frame_length) / frame_shift + 1;
            normDbuf = new double[frame_length_half];
            CreateReadData data = new CreateReadData();
            input_win = new Window_function().Hanning_Window(frame_length);
            data.Process("IPD", (int)border_n, out IPD, out P_range);
            data.Process("ILD", (int)border_n, out ILD, out L_range);
            normparameterbuf = Enumerable.Repeat<double>(80, 256).ToArray();
            output_l = new double[len_l];
            output_r = new double[len_r];

            input_l_list = new double[len_l];
            input_r_list = new double[len_r];
            output_l_list = new double[len_l];
            output_r_list = new double[len_r];
        }

        public void Signal_segregation(double[] input_l, double[] input_r, int target_beam, int num)
        {
            int st, en;
            Complex[] data_l = new Complex[frame_length];
            Complex[] data_r = new Complex[frame_length];
            Complex[] DL = new Complex[frame_length];
            double[] DL_R = new double[frame_length];
            double[] DL_I = new double[frame_length];
            Complex[] DR = new Complex[frame_length];
            double[] DR_R = new double[frame_length];
            double[] DR_I = new double[frame_length];
            Complex[] recovl = new Complex[frame_length];
            Complex[] recovr = new Complex[frame_length];

            int st2;

            target_beam /= 10;
            Ar_target_beam[num] = target_beam;

            for (int i = 0; i < numofloop; i++)
            {
                st = frame_shift * i;
                en = st + frame_length;
                for (int j = 0; j < frame_length; j++)
                {
                    data_l[j] = input_l[st + j] * input_win[j];
                    data_r[j] = input_r[st + j] * input_win[j];
                }

                double[] Im = new double[frame_length];
                MathNet.Numerics.IntegralTransforms.Fourier.Forward(data_l, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
                MathNet.Numerics.IntegralTransforms.Fourier.Forward(data_r, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);

                double[] PD = new double[frame_length];
                double[] LD = new double[frame_length];

                Array.Copy(data_l, 0, DL, 0, data_l.Length);
                Array.Copy(data_r, 0, DR, 0, data_r.Length);

                Complex buf;
                for (int j = 0; j < frame_length; j++)
                {
                    buf = DR[j] * Complex.Conjugate(DL[j]);
                    PD[j] = buf.Phase;
                    LD[j] = 20 * Math.Log10(Complex.Abs(DR[j] + 0.0000001) / Complex.Abs(DL[j] + 0.0000001));
                }

                double[] normD = new double[frame_length >> 1];

                double theta;
                for (int j = 1; j < border_n; j++)
                {
                    theta = Math.Abs(IPD[target_beam, j] - PD[j]);
                    normD[j] = theta / P_range[j];

                    if (normD[j] > 1)
                    {
                        normD[j] = 1;
                    }

                    if (normD[j] < 0.0001)
                    {
                        normD[j] = 0.0001;
                    }
                }

                double level;
                for (int j = (int)border_n; j < frame_length_half; j++)
                {
                    level = Math.Abs(ILD[target_beam, j] - LD[j]);
                    normD[j] = level / L_range[j];
                }

                var rnd = new Random();

                for (int j = 0; j < normD6.Length; j++)
                {
                    normD6[j] = 1 / (1 + Math.Exp(normparameterbuf[j] * normD[j] - 10)) + Math.Abs(rnd.NextDouble()) * filternoise;
                }

                if (binarymask == 1)
                {
                    for (int j = 0; j < normD6.Length; j++)
                    {
                        if (normD6[j] < 0.3)
                        {
                            normD6[j] = 0; //Contrast
                        }
                        else
                        {
                            normD6[j] = 1;
                        }
                    }
                }

                for (int j = 0; j < normDbuf.Length; j++)
                {
                    normDbuf[j] = normDbuf[j] * forgetting + normD6[j] * (1 - forgetting);
                }

                st2 = en - adelay;
                Array.Copy(normDbuf, 0, normD62, 0, frame_length_half);
                normD62[frame_length_half] = 0;
                Array.Copy(normDbuf.Reverse().ToArray(), 0, normD62, frame_length_half + 1, frame_length_half - 1);

                Complex d1, d2;
                for (int j = 0; j < DL.Length; j++)
                {
                    d1 = DL[j] * normD62[j];
                    DL_R[j] = d1.Real;
                    DL_I[j] = d1.Imaginary;
                    d2 = DR[j] * normD62[j];
                    DR_R[j] = d2.Real;
                    DR_I[j] = d2.Imaginary;
                }

                Window_function.IFFT(DL_R, DL_I, out recovl, frame_length);
                Window_function.IFFT(DR_R, DR_I, out recovr, frame_length);

                for (int j = 0; j < frame_length; j++)
                {
                    output_l[st2 + j] = output_l[st2 + j] + recovl[j].Real * input_win[j];
                    output_r[st2 + j] = output_r[st2 + j] + recovr[j].Real * input_win[j];
                }
            }

            double sigmax = Math.Max(output_l.Max(), output_r.Max()) * 1.05;
            if (target_beam > 9) //front 90 right 180
            {
                _output = new double[input_r.Length];
                for (int j = 0; j < input_r.Length; j++)
                {
                    _output[j] = output_r[j] / sigmax;// / (frame_length / frame_shift / 4);
                }
            }
            else if (target_beam < 9) //front 90 left 0
            {
                _output = new double[input_l.Length];
                for (int j = 0; j < input_l.Length; j++)
                {
                    _output[j] = output_l[j] / sigmax;// / (frame_length / frame_shift / 4);
                }
            }
            else
            {
                _output = new double[input_l.Length];
                for (int j = 0; j < input_l.Length; j++)
                {
                    _output[j] = output_l[j] / sigmax;// / (frame_length / frame_shift / 4);
                }
            }
        }

        public void Signal_segregation_frame(double[] input_l, double[] input_r, int target_beam, int num)
        {
            int st, en;
            Complex[] data_l = new Complex[frame_length];
            Complex[] data_r = new Complex[frame_length];
            Complex[] DL = new Complex[frame_length];
            double[] DL_R = new double[frame_length];
            double[] DL_I = new double[frame_length];
            Complex[] DR = new Complex[frame_length];
            double[] DR_R = new double[frame_length];
            double[] DR_I = new double[frame_length];
            Complex[] recovl = new Complex[frame_length];
            Complex[] recovr = new Complex[frame_length];

            output_l = new double[input_l.Length];
            output_r = new double[input_r.Length];

            int st2;

            target_beam /= 10;
            Ar_target_beam[num] = target_beam;

            Array.Copy(input_l_list, frame_length, input_l_list, 0, frame_length);
            Array.Copy(input_r_list, frame_length, input_r_list, 0, frame_length);

            Array.Copy(input_l, 0, input_l_list, frame_length, input_l.Length);
            Array.Copy(input_r, 0, input_r_list, frame_length, input_r.Length);

            for (int i = 0; i < numofloop - 1; i++)
            {
                st = frame_shift * i;
                en = st + frame_length;
                for (int j = 0; j < frame_length; j++)
                {
                    data_l[j] = input_l_list[st + j] * input_win[j];
                    data_r[j] = input_r_list[st + j] * input_win[j];
                }

                MathNet.Numerics.IntegralTransforms.Fourier.Forward(data_l, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
                MathNet.Numerics.IntegralTransforms.Fourier.Forward(data_r, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);

                double[] PD = new double[frame_length];
                double[] LD = new double[frame_length];

                Array.Copy(data_l, 0, DL, 0, data_l.Length);
                Array.Copy(data_r, 0, DR, 0, data_r.Length);

                Complex buf;
                for (int j = 0; j < frame_length; j++)
                {
                    buf = DR[j] * Complex.Conjugate(DL[j]);
                    PD[j] = buf.Phase;
                    LD[j] = 20 * Math.Log10(Complex.Abs(DR[j] + 0.0000001) / Complex.Abs(DL[j] + 0.0000001));
                }

                double[] normD = new double[frame_length >> 1];

                double theta;
                for (int j = 1; j < border_n; j++)
                {
                    theta = Math.Abs(IPD[target_beam, j] - PD[j]);
                    normD[j] = theta / P_range[j];

                    if (normD[j] > 1)
                    {
                        normD[j] = 1;
                    }

                    if (normD[j] < 0.0001)
                    {
                        normD[j] = 0.0001;
                    }
                }

                double level;
                for (int j = (int)border_n; j < frame_length_half; j++)
                {
                    level = Math.Abs(ILD[target_beam, j] - LD[j]);
                    normD[j] = level / L_range[j];
                }

                var rnd = new Random();

                for (int j = 0; j < normD6.Length; j++)
                {
                    normD6[j] = 1 / (1 + Math.Exp(normparameterbuf[j] * normD[j] - 10)) + Math.Abs(rnd.NextDouble()) * filternoise;
                }

                if (binarymask == 1)
                {
                    for (int j = 0; j < normD6.Length; j++)
                    {
                        if (normD6[j] < 0.3)
                        {
                            normD6[j] = 0; //Contrast
                        }
                        else
                        {
                            normD6[j] = 1;
                        }
                    }
                }

                for (int j = 0; j < normDbuf.Length; j++)
                {
                    normDbuf[j] = normDbuf[j] * forgetting + normD6[j] * (1 - forgetting);
                }

                st2 = en - adelay;
                Array.Copy(normDbuf, 0, normD62, 0, frame_length_half);
                normD62[frame_length_half] = 0;
                Array.Copy(normDbuf.Reverse().ToArray(), 0, normD62, frame_length_half + 1, frame_length_half - 1);

                Complex d1, d2;
                for (int j = 0; j < DL.Length; j++)
                {
                    d1 = DL[j] * normD62[j];
                    DL_R[j] = d1.Real;
                    DL_I[j] = d1.Imaginary;
                    d2 = DR[j] * normD62[j];
                    DR_R[j] = d2.Real;
                    DR_I[j] = d2.Imaginary;
                }

                Window_function.IFFT(DL_R, DL_I, out recovl, frame_length);
                Window_function.IFFT(DR_R, DR_I, out recovr, frame_length);

                for (int j = 0; j < frame_length; j++)
                {
                    output_l_list[st2 + j] = output_l_list[st2 + j] + recovl[j].Real * input_win[j];
                    output_r_list[st2 + j] = output_r_list[st2 + j] + recovr[j].Real * input_win[j];
                }
            }
            var test = Math.Max(output_l_list.Max(), output_r_list.Max()) * 1.05;
            sigmax = sigmax <= test ? test : sigmax * 0.8 ;//1138.7629772175;
            if (target_beam > 9) //front 90 right 180
            {
                _output = new double[frame_length];
                for (int j = 0; j < frame_length; j++)
                {
                    _output[j] = output_r_list[j] / sigmax;// / (frame_length / frame_shift / 4);
                }
            }
            else if (target_beam < 9) //front 90 left 0
            {
                _output = new double[frame_length];
                for (int j = 0; j < frame_length; j++)
                {
                    _output[j] = output_l_list[j] / sigmax;// / (frame_length / frame_shift / 4);
                }
            }
            else
            {
                _output = new double[frame_length];
                for (int j = 0; j < frame_length; j++)
                {
                    _output[j] = output_l_list[j] / sigmax;// / (frame_length / frame_shift / 4);
                }
            }
            Array.Copy(output_l_list, frame_length, output_l_list, 0, frame_length);
            Array.Copy(output_r_list, frame_length, output_r_list, 0, frame_length);

            Array.Copy(Enumerable.Repeat<double>(0, frame_length).ToArray(), 0, output_l_list, frame_length, frame_length);
            Array.Copy(Enumerable.Repeat<double>(0, frame_length).ToArray(), 0, output_r_list, frame_length, frame_length);
        }

    }

    /// <summary>
    /// Hanning window
    /// FFT / IFFT
    /// </summary>
    class Window_function
    {
        double[] hanning_window;

        public Window_function()
        {

        }

        public double[] Hanning_Window(int length)
        {
            hanning_window = new double[length];
            if (length % 2 == 0)
            {
                int _half = length / 2;
                return han(_half * 2, length);
            }
            else
            {
                int _half = (length + 1) / 2;
                return han(_half * 2, length);
            }
        }

        private double[] han(int m, int n)
        {
            for (int i = 1; i < m + 1; i++)
            {
                hanning_window[i - 1] = 5 * (1 - Math.Cos(2 * Math.PI * i / (n + 1)));
            }
            return hanning_window;
        }

        /// <summary>
        /// This FFT used the thinned-out time
        /// </summary>
        /// <param name="input_r"></param>
        /// <param name="input_i"></param>
        /// <param name="output"></param>
        /// <param name="frame_length"></param>
        public static void FFT(double[] input_r, double[] input_i, out Complex[] output, int frame_length)
        {
            output = new Complex[frame_length];

            int[] reverse_array = BTR(frame_length);

            for (int i = 0; i < frame_length; i++)
            {
                output[i] = new Complex(input_r[reverse_array[i]], input_i[reverse_array[i]]);
            }

            int bit_size = (int)Math.Log(frame_length, 2);
            int butter_fly_distance;
            int num;
            int butter_fly_size;

            double w_r;
            double w_i;

            double x_r;
            double x_i;

            int jp;
            double temp_r;
            double temp_i;
            double wtemp_r;
            double wtemp_i;

            for (int i = 0; i <= bit_size; i++)
            {
                butter_fly_distance = 1 << i;
                num = butter_fly_distance >> 1;
                butter_fly_size = butter_fly_distance >> 1;

                w_r = 1.0;
                w_i = 0.0;

                x_r = Math.Cos(Math.PI / butter_fly_size);
                x_i = -Math.Sin(Math.PI / butter_fly_size);

                for (int n = 0; n < num; n++)
                {
                    for (int j = n; j < frame_length; j += butter_fly_distance)
                    {
                        jp = j + butter_fly_size;
                        temp_r = output[jp].Real * w_r - output[jp].Imaginary * w_i;
                        temp_i = output[jp].Real * w_i + output[jp].Imaginary * w_r;

                        output[jp] = output[j] - new Complex(temp_r, temp_i);
                        output[j] = output[j] + new Complex(temp_r, temp_i);
                    }
                    wtemp_r = w_r * x_r - w_i * x_i;
                    wtemp_i = w_r * x_i - w_i * x_r;
                    w_r = wtemp_r;
                    w_i = wtemp_i;
                }
            }
        }

        public static void IFFT(double[] input_r, double[] input_i, out Complex[] output, int frame_length)
        {
            output = new Complex[frame_length];

            for (int i = 0; i < frame_length; i++)
            {
                input_i[i] = -input_i[i];
            }

            MathNet.Numerics.IntegralTransforms.Fourier.Forward(input_r, input_i, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);

            for (int i = 0; i < frame_length; i++)
            {
                input_r[i] /= (double)frame_length;
                input_i[i] /= (double)(-frame_length);

                output[i] = new Complex(input_r[i], input_i[i]);
            }
        }

        public static int[] BTR(int bit_size)
        {
            int[] Bit_array = new int[bit_size];
            int half_bit_size = bit_size >> 1;

            Bit_array[0] = 0;
            for (int i = 1; i < bit_size; i <<= 1)
            {
                for (int j = 0; j < i; j++)
                {
                    Bit_array[j + i] = Bit_array[j] + half_bit_size;
                }
                half_bit_size >>= 1;
            }
            return Bit_array;
        }
    }

    class Array_Converter
    {
        static public byte[] Short_to_Byte(short[] buf)
        {
            byte[] _After = new byte[buf.Length * 2];
            byte[] _tmp = new byte[2];
            int i = 0;
            //StreamWriter sw = new StreamWriter(@".\Test\text.txt", true, Encoding.GetEncoding("Shift_JIS"));
            foreach (short _d in buf)
            {
                _tmp = BitConverter.GetBytes(_d);
                _After[i] = _tmp[0];
                _After[i + 1] = _tmp[1];
                i += 2;
            }
            return _After;
        }

        static public byte[] Double_to_Byte(double[] buf)
        {
            byte[] _After = new byte[buf.Length * 2];
            byte[] _tmp = new byte[2];
            int i = 0;
            //StreamWriter sw = new StreamWriter(@".\Test\text.txt", true, Encoding.GetEncoding("Shift_JIS"));
            foreach (double _d in buf)
            {
                _tmp = BitConverter.GetBytes(D_To_S(_d));
                _After[i] = _tmp[0];
                _After[i + 1] = _tmp[1];
                i += 2;
            }
            return _After;
        }

        static private short D_To_S(double s)
        {
            var s_d = s * short.MaxValue;
            if (s_d > short.MaxValue)
            {
                s_d = short.MaxValue * 0.9;
            }
            if (s_d < short.MinValue)
            {
                s_d = short.MinValue * 0.9;
            }

            return (short)s_d;
        }

    }

    class Program
    {
        //Man:+40, Woman:0
        static void Main(string[] args)
        {
            bool flg = true;
            int[] Ar_angle = { 90 };
            Seg _Segregation = new Seg();
            string _Path = @".\Test\source.wav";//正面(90度)女性　右側(140度)男性
            Wave_file_cr wavefile = new Wave_file_cr();
            wavefile.Wave_File_Binary(_Path);
            //_Segregation.Seg_initial_set_param(wavefile.File_Data_L.Length, wavefile.File_Data_R.Length);
            const int data_M = 512;
            _Segregation.Seg_initial_set_param(data_M * 2, data_M * 2);
            Console.Write("角度指定：");
            for (int i = 0; flg; i++)
            {
                int _angle = 0;
                flg = int.TryParse(Console.ReadLine(), out _angle);
                if (flg)
                {

                    var before_ = System.DateTime.Now;
                    for (int index = 0; index < (wavefile.File_Data_L.Length / data_M) + 1; index++)
                    {
                        if (wavefile.File_Data_L.Length > ((index + 1) * data_M))
                        {
                            double[] data_pac_L = new double[data_M];
                            Array.Copy(wavefile.File_Data_L, index * data_M, data_pac_L, 0, data_M);
                            double[] data_pac_R = new double[data_M];
                            Array.Copy(wavefile.File_Data_R, index * data_M, data_pac_R, 0, data_M);

                            _Segregation.Signal_segregation_frame(data_pac_L, data_pac_R, _angle, i);
                            wavefile.File_data_list.AddRange(Array_Converter.Double_to_Byte(_Segregation._output));
                        }
                        else
                        {
                            double[] data_pac_L = new double[data_M];
                            Array.Copy(wavefile.File_Data_L, index * data_M, data_pac_L, 0, wavefile.File_Data_L.Length - index * data_M);
                            double[] data_pac_R = new double[data_M];
                            Array.Copy(wavefile.File_Data_R, index * data_M, data_pac_R, 0, wavefile.File_Data_R.Length - index * data_M);

                            _Segregation.Signal_segregation_frame(data_pac_L, data_pac_R, _angle, i);
                            wavefile.File_data_list.AddRange(Array_Converter.Double_to_Byte(_Segregation._output));
                        }
                    }
                    var after_ = System.DateTime.Now - before_;
                    Console.WriteLine(after_.TotalMilliseconds);
                    wavefile.File_data = wavefile.File_data_list.ToArray();
                    wavefile.File_data_list.Clear();
                    /*
                    var before_ =  System.DateTime.Now;
                    _Segregation.Signal_segregation(wavefile.File_Data_L, wavefile.File_Data_R, _angle, i);
                    var after_ = System.DateTime.Now - before_;
                    Console.WriteLine(after_.TotalMilliseconds);
                    wavefile.File_data = Array_Converter.Double_to_Byte(_Segregation._output);
                    */
                    wavefile.Write_wav_f(_angle);
                }
                else
                {
                    Console.WriteLine("Input data without range");
                    Console.ReadLine();
                    throw new ArgumentOutOfRangeException();
                }
            }
            Console.WriteLine("end");
            Console.ReadLine();

        }
    }
}
