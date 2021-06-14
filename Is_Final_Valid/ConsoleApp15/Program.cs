using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Numerics;
using ClosedXML.Excel;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.IO;
using Google.Apis.Auth.OAuth2;
using Grpc.Core;
using Grpc.Auth;
using Google.Cloud.Speech.V1;
using System.Timers;
using NAudio;
using Create_Recognition;
//using CommandLine;
//10秒以上受信間隔を開けるとエラーが発生する。

namespace Create_Recognition
{

    public static class TaskEx
    {
        // ロガーはここではNLogを使うとします
        private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 投げっぱなしにする場合は、これを呼ぶことでコンパイラの警告の抑制と、例外発生時のロギングを行います。
        /// </summary>
        public static void FireAndForget(this Task task)
        {
            task.ContinueWith(x =>
            {
                logger.Error("TaskUnhandled", x.Exception);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    class Gcs
    {
        string Json_file { get; set; }
        byte[] Segdata { get; set; }
        GoogleCredential credential;
        Channel channel;
        Speech.SpeechClient client;
        StreamingRecognitionConfig streaming_config;

        List<byte> Recognizerbuffer = new List<byte>();
        private DateTime _rpcStreamDeadline;

        SpeechClient speech;
        private SpeechClient.StreamingRecognizeStream streamingCall = null;
        private string ip;
        private TimeSpan s_streamTimeLimit;

        Wave_file_cr wavefile;
        Seg _Segregator;
        UdpClient clt;

        object writeLock;
        bool writeMore;

        public Gcs(string ip, TimeSpan s_streamTimeLimit)
        {
            this.ip = ip;
            this.s_streamTimeLimit = s_streamTimeLimit;
            Console.WriteLine("チャンネルセットアップ");
            credential = GoogleCredential.FromJson(File.ReadAllText(@""));
            credential = credential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
            channel = new Channel("speech.googleapis.com", 443, credential.ToChannelCredentials());
            client = new Speech.SpeechClient(channel);
            speech = SpeechClient.Create(channel);
            Console.WriteLine("セットアップ終了");
        }

        // PCマイク入力による音声データに対してストリーミング音声認識を行う
        Task printResponses = null;
        public async Task transcibe_streaming(byte[] buf)
        //<object>
        {
            var now = DateTime.UtcNow;

            //終了時間判定
            if (streamingCall != null && now >= _rpcStreamDeadline)
            {
                Recognizerbuffer.AddRange(buf);
                await streamingCall.WriteCompleteAsync();
                await printResponses;
                streamingCall.GrpcCall.Dispose();
                streamingCall = null;
            }

            //実行中であれば返す
            if (streamingCall != null)
            {
                writeLock = new object();
                writeMore = true;
                streamingCall.WriteAsync(
                new StreamingRecognizeRequest()
                {
                    AudioContent = Google.Protobuf.ByteString.CopyFrom(buf, 0, buf.Length)
                    //AudioContent = Google.Protobuf.ByteString.CopyFrom(wavefile.File_data, 0, wavefile.File_data.Length)
                }).Wait();
                //Console.WriteLine("送信終了" + System.DateTime.Now.ToString("hh.mm.ss.ffffff"));
                lock (writeLock) writeMore = false;
                //Recognizerbuffer.Clear();

                return;
            }

            Console.WriteLine("サーバー接続開始");
            streamingCall = speech.StreamingRecognize();

            _rpcStreamDeadline = now + s_streamTimeLimit;

            // 初期リクエストの設定
            await streamingCall.WriteAsync(
                new StreamingRecognizeRequest()
                {
                    StreamingConfig = new StreamingRecognitionConfig()
                    {
                        Config = new RecognitionConfig()
                        {
                            Encoding =
                            RecognitionConfig.Types.AudioEncoding.Linear16,
                            SampleRateHertz = 16000,
                            LanguageCode = "ja-JP",
                            EnableAutomaticPunctuation = true,
                            Model = "default",
                            //AudioChannelCount = 2,

                        },
                        //InterimResults = false,
                        InterimResults = true, //中間結果を返す． 
                        SingleUtterance = false
                    }
                });

            UDP_UdpClient udp = new UDP_UdpClient(ip, 3333);
            // リクエストの到着（受信）の応答を返す
            printResponses = Task.Run(async () =>
            {
                //MoveNext一回につきレスポンス1回分のデータが返ってくる
                while (await streamingCall.ResponseStream.MoveNext())
                {
                    StreamingRecognizeResponse response = streamingCall.ResponseStream.Current;
                    foreach (var result in response.Results)
                    {
                        Console.WriteLine(result.IsFinal);
                        if (result.IsFinal)
                        {
                            Console.WriteLine("Final");
                            foreach (var alternative in result.Alternatives)
                            {
                                udp._Send("is_final:" + alternative.Transcript).FireAndForget();
                                Console.WriteLine("結果:" + "is_final:" + alternative.Transcript);
                            }
                        }
                        else
                        {
                            foreach (var alternative in result.Alternatives)
                            {
                                udp._Send(alternative.Transcript).FireAndForget();
                                Console.WriteLine("結果:" + alternative.Transcript);
                            }
                        }
                    }
                }
            });

            Console.WriteLine("サーバー接続完了");

            if (Recognizerbuffer.Count != 0)
            {
                writeLock = new object();
                writeMore = true;
                streamingCall.WriteAsync(
                               new StreamingRecognizeRequest()
                               {
                                   AudioContent = Google.Protobuf.ByteString.CopyFrom(Recognizerbuffer.ToArray(), 0, Recognizerbuffer.Count())
                                   //AudioContent = Google.Protobuf.ByteString.CopyFrom(wavefile.File_data, 0, wavefile.File_data.Length)
                               }).Wait();
                Console.WriteLine("送信終了" + System.DateTime.Now.ToString("hh.mm.ss.ffffff"));
                lock (writeLock) writeMore = false;
                Recognizerbuffer.Clear();
            }
            return;
        }

        public async Task transcibe_streaming()
        //<object>
        {
            var now = DateTime.UtcNow;
            Console.WriteLine("サーバー接続初期設定");
            streamingCall = speech.StreamingRecognize();

            _rpcStreamDeadline = now + s_streamTimeLimit;

            // 初期リクエストの設定
            await streamingCall.WriteAsync(
                new StreamingRecognizeRequest()
                {
                    StreamingConfig = new StreamingRecognitionConfig()
                    {
                        Config = new RecognitionConfig()
                        {
                            Encoding =
                            RecognitionConfig.Types.AudioEncoding.Linear16,
                            SampleRateHertz = 16000,
                            LanguageCode = "ja-JP",
                            EnableAutomaticPunctuation = true,
                            Model = "default",
                            //AudioChannelCount = 2,

                        },
                        //InterimResults = false,
                        InterimResults = true, //中間結果を返す． 
                        SingleUtterance = false
                    }
                });

            UDP_UdpClient udp = new UDP_UdpClient(ip, 3333);
            // リクエストの到着（受信）の応答を返す
            printResponses = Task.Run(async () =>
            {
                //MoveNext一回につきレスポンス1回分のデータが返ってくる
                while (await streamingCall.ResponseStream.MoveNext())
                {
                    StreamingRecognizeResponse response = streamingCall.ResponseStream.Current;
                    foreach (var result in response.Results)
                    {
                        Console.WriteLine(result.IsFinal);
                        if (result.IsFinal)
                        {
                            Console.WriteLine("Final");
                            foreach (var alternative in result.Alternatives)
                            {
                                udp._Send("is_final:" + alternative.Transcript).FireAndForget();
                                Console.WriteLine("結果:" + "is_final:" + alternative.Transcript);
                            }
                        }
                        else
                        {
                            foreach (var alternative in result.Alternatives)
                            {
                                udp._Send(alternative.Transcript).FireAndForget();
                                Console.WriteLine("結果:" + alternative.Transcript);
                            }
                        }
                    }
                }
            });

            Console.WriteLine("サーバー接続完了");
            return;
        }

        private byte[] Source_sep(Wave_file_cr wavefile, Seg _Segregator, int angle)
        {
            wavefile.Udp_Progress_D();
            _Segregator.Seg_initial_set_param(wavefile.File_Data_L.Length, wavefile.File_Data_R.Length);
            _Segregator.Signal_segregation(wavefile.File_Data_L, wavefile.File_Data_R, angle, 0);
            return Array_Converter.Double_to_Byte(_Segregator._output);
        }

        private async Task Send_speech(byte[] buf)
        {
            await Task.Run(() =>
            {
                for (int index = 0; index < (int)(buf.Length / 512); index++)
                {
                    if (buf.Length > ((index + 1) * 512))
                    {
                        byte[] data_pac = new byte[512];

                        Array.Copy(buf, index * 512, data_pac, 0, 512);
                        lock (writeLock)
                        {
                            if (!writeMore) return;

                            streamingCall.WriteAsync(
                               new StreamingRecognizeRequest()
                               {
                                   AudioContent = Google.Protobuf.ByteString.CopyFrom(data_pac, 0, data_pac.Length)
                                   //AudioContent = Google.Protobuf.ByteString.CopyFrom(wavefile.File_data, 0, wavefile.File_data.Length)
                               }).Wait();
                        }
                    }
                    else
                    {
                        byte[] data_pac = new byte[512];

                        Array.Copy(buf, index * 512, data_pac, 0, buf.Length - (index * 512));
                        lock (writeLock)
                        {
                            if (!writeMore) return;

                            streamingCall.WriteAsync(
                               new StreamingRecognizeRequest()
                               {
                                   AudioContent = Google.Protobuf.ByteString.CopyFrom(data_pac, 0, data_pac.Length)
                                   //AudioContent = Google.Protobuf.ByteString.CopyFrom(wavefile.File_data, 0, wavefile.File_data.Length)
                               }).Wait();
                        }
                    }
                }
                Console.WriteLine("送信終了" + System.DateTime.Now.ToString("hh.mm.ss.ffffff"));
            });
        }


        private async Task Sep_process(Wave_file_cr wavefile, Seg _Segregator, int angle, SpeechClient.StreamingRecognizeStream streamingCall)
        {
            await Task.Run(() =>
            {
                wavefile.Udp_Progress_D();
                _Segregator.Seg_initial_set_param(wavefile.File_Data_L.Length, wavefile.File_Data_R.Length);
                _Segregator.Signal_segregation(wavefile.File_Data_L, wavefile.File_Data_R, angle, 0);
                byte[] buf = Array_Converter.Double_to_Byte(_Segregator._output);

                streamingCall.WriteAsync(
                               new StreamingRecognizeRequest()
                               {
                                   AudioContent = Google.Protobuf.ByteString.CopyFrom(buf, 0, buf.Length)
                               }).Wait();
                Console.WriteLine("送信終了" + System.DateTime.Now.ToString("hh.mm.ss.ffffff"));
            });
        }

        private async Task RunAsync()
        {
            wavefile = new Wave_file_cr("");
            _Segregator = new Seg();
            int angle = 0;
            int frame_cnt = 0;
            int port = 2002;
            IPEndPoint localEP = new IPEndPoint(IPAddress.Any, port);
            await transcibe_streaming(); //初期設定
            using (clt = new UdpClient(localEP))
            {
                while (true)
                {
                    var result = await clt.ReceiveAsync();
                    //Console.WriteLine("受信開始:" + System.DateTime.Now.ToString("hh.mm.ss.ffffff"));
                    frame_cnt++;
                    wavefile.Wave_data(result.Buffer);
                    if (frame_cnt == 4)
                    {
                        frame_cnt = 0;
                        byte[] buf = wavefile.StereoToMono();
                        //Console.WriteLine("受信終了＋分離開始:" + System.DateTime.Now.ToString("hh.mm.ss.ffffff"));
                        //byte[] buf = Source_sep(wavefile, _Segregator, angle);
                        //Console.WriteLine("分離終了＋送信開始" + System.DateTime.Now.ToString("hh.mm.ss.ffffff"));
                        await transcibe_streaming(buf);
                        wavefile.Wave_data_clear();
                    }

                }
            }
        }

        public static async Task<int> RecognizeAsync(string ip, TimeSpan s_streamTimeLimit)
        {
            var instance = new Gcs(ip, s_streamTimeLimit);
            await instance.RunAsync();
            return 0;
        }
    }
}
class Wave_file_cr
{
    short headerSize = 44;
    short channels = 1;
    short bitsPerSample = 16;
    int sampleRate = 16000;
    int datasize;

    public byte[] _wav { get; set; }
    byte[] stock_data;
    public List<byte> data;

    short _seq = -1;
    int delay_seq = 10;

    public byte[] File_data { get; set; }
    public byte[] DATA { get; set; }
    public byte[] Buffer { get; set; }
    List<byte> monodata;
    /// <summary>
    /// The reason to convert short type to double type is
    ///         to prepare for appropriate process in Seg
    /// </summary>
    public double[] File_Data { get; private set; }
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

    public Wave_file_cr(string Type)
    {
        _wav = new byte[headerSize];
        data = new List<byte>();
        monodata = new List<byte>();
        stock_data = new byte[550];
        if (Type == "local")
        {
            _waveheader = new WaveHeader();
        }
    }

    void Wave_header_2ch(int sampledatabytesize)
    {
        channels = 2;
        datasize = sampledatabytesize;

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
        byte[] b_rate = BitConverter.GetBytes(sampleRate * (short)(bitsPerSample / 8 * channels));
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

                    File_Data = new double[_waveheader.Data_Chunk_Size / 2];
                    File_Data_L = new double[_waveheader.Data_Chunk_Size / 4];
                    File_Data_R = new double[_waveheader.Data_Chunk_Size / 4];
                    int i = 0;
                    for (int j = 0; j < b.Length - 4; j += 4)
                    {
                        File_Data[i] = (double)BitConverter.ToInt16(b, j / 2) / short.MaxValue;
                        File_Data_L[i] = (double)BitConverter.ToInt16(b, j) / short.MaxValue;
                        File_Data_R[i] = (double)BitConverter.ToInt16(b, j + 2) / short.MaxValue;
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

    public void Wave_data_clear()
    {
        data.Clear();
    }

    /*
        public List<byte> StereoToMono()
        {
            byte[] b = data.ToArray();
            DATA = new byte[b.Length / 2];
            int i = 0;
            for (int j = 0; j < DATA.Length; j += 4)
            {
                DATA[i] = b[j];
                DATA[++i] = b[j + 1];
                i++;
            }
            monodata.AddRange(DATA);
            return monodata;
        }
        */
    public byte[] StereoToMono()
    {
        byte[] b = data.ToArray();
        DATA = new byte[b.Length / 2];
        int i = 0;
        for (int j = 0; j < DATA.Length; j += 4)
        {
            DATA[i] = b[j];
            DATA[++i] = b[j + 1];
            i++;
        }
        return DATA;
    }

    public byte[] StereoToMono(List<byte> data)
    {
        byte[] b = data.ToArray();
        DATA = new byte[b.Length / 2];
        int i = 0;
        for (int j = 0; j < DATA.Length; j += 4)
        {
            DATA[i] = b[j];
            DATA[++i] = b[j + 1];
            i++;
        }
        return DATA;
    }

    public byte[] ToMono(List<byte> data)
    {
        byte[] b = data.ToArray();
        //byte[] newData = new byte[b.Length / 2];
        byte[] DATA = new byte[b.Length / 2];

        for (int i = 0; i < b.Length / 4; ++i)
        {
            int HI = 1; int LO = 0;
            short left = (short)((b[i * 4 + HI] << 8) | (b[i * 4 + LO] & 0xff));
            short right = (short)((b[i * 4 + 2 + HI] << 8) | (b[i * 4 + 2 + LO] & 0xff));
            int avg = (left + right) / 2;

            DATA[i * 2 + HI] = (byte)((avg >> 8) & 0xff);
            DATA[i * 2 + LO] = (byte)((avg & 0xff));
        }
        return DATA;
    }

    public void Get()
    {
        File_data = monodata.ToArray();
    }

    public bool _jd(short d)
    {
        _seq = 0;
        return d == short.MinValue ? true : false;
    }

    public void Udp_Progress_D()
    {
        byte[] b = data.ToArray();
        File_Data = new double[b.Length / 2];
        File_Data_L = new double[b.Length / 4];
        File_Data_R = new double[b.Length / 4];
        int i = 0;
        for (int j = 0; j <= (b.Length - 4); j += 4)
        {
            File_Data[i] = (double)BitConverter.ToInt16(b, j / 2) / short.MaxValue;
            File_Data_L[i] = (double)BitConverter.ToInt16(b, j) / short.MaxValue;
            File_Data_R[i] = (double)BitConverter.ToInt16(b, j + 2) / short.MaxValue;
            i++;
        }
    }

    public void Write_wav_f(string type, int name)
    {
        string file = string.Format(@".\Test\text{0}.wav", name);
        if (type == "local")
        {
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
        else if (type == "udp")
        {
            Wave_header(File_data.Length);
            using (FileStream sw = new FileStream(file, FileMode.Create, FileAccess.Write))
            {
                sw.Write(_wav, 0, _wav.Length);
                Console.WriteLine("data");

                sw.Write(File_data, 0, File_data.Length);
                Console.WriteLine("finish!!");
                //sw.Close();
            }
        }
        else
        {
            file = @".\Test\text.wav";
            Wave_header_2ch(data.Count);

            using (FileStream sw = new FileStream(file, FileMode.Create, FileAccess.Write))
            {
                sw.Write(_wav, 0, _wav.Length);
                Console.WriteLine("data");

                sw.Write(data.ToArray(), 0, data.ToArray().Length);
                Console.WriteLine("finish!!");
                //sw.Close();
            }
        }
    }

}

class UDP_UdpClient
{
    IPEndPoint remote;
    byte[] sendMsg;
    UdpClient client;

    public UDP_UdpClient(string IP, int Port)
    {
        remote = new IPEndPoint(IPAddress.Parse(IP), Port);
        client = new UdpClient();
        client.Connect(remote);
    }

    public async Task _Send(string text)
    {
        sendMsg = Encoding.UTF8.GetBytes(text);
        //await client.SendAsync(sendMsg, sendMsg.Length);
        await Task.Run(() =>
        {
            try
            {
                client.Send(sendMsg, sendMsg.Length);
            }
            catch
            {
                Console.WriteLine("Send error");
            }
        });
    }
}

interface IData_Type
{
    double[,] Read_Data();
    double[] DataRange();
    double[,] Gain_parameter(int border_n);
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

class Get_IPD : IData_Type
{
    double[,] IPD;
    double[,] _IPD;
    double[] Prange;
    double[,] normparameterbuf;
    static int flen = 512;
    static int border_n = 750 / (16000 / flen) + 1;

    public Get_IPD()
    {
        IPD = new double[37, border_n];
        _IPD = new double[37, border_n];
        normparameterbuf = new double[37, border_n];
        Prange = new double[flen / 2];
    }

    public double[,] Read_Data()
    {
        XLWorkbook workbook1 = new XLWorkbook(string.Format(@".\IPD{0}.xlsx", flen));
        IXLWorksheet worksheet1 = workbook1.Worksheet(1);
        IXLCell cell;
        int lastRow1 = worksheet1.LastRowUsed().RowNumber();
        for (int i = 0; i < 37; i++)
        {
            for (int j = 0; j < border_n; j++)
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
        for (int i = 0; i < border_n; i++)
        {
            buf = two_dim.Get_Array("row", i, 37);
            Prange[i] = buf.Max() - buf.Min();
        }
        return Prange;
    }

    /// <summary>
    /// No need
    /// </summary>
    /// <param name="border_n"></param>
    /// <param name="_data"></param>
    /// <param name="_datarange"></param>
    /// <returns></returns>
    public double[,] Gain_parameter(int border_n)
    {
        double diffmean;
        double defaultvalue = 500 * 2 * 2;

        Prange[0] = 0.00001;

        for (int i = 0; i < border_n; i++)
        {
            for (int j = 1; j < 18; j++)
            {
                diffmean = Math.Abs(IPD[j + 1, i] - IPD[j - 1, i]) / 2;
                normparameterbuf[i, j] = defaultvalue * (diffmean / Prange[i]);
            }
            diffmean = Math.Abs(IPD[1, i] - IPD[0, i]);
            normparameterbuf[i, 0] = defaultvalue * (diffmean / Prange[i]);
            diffmean = Math.Abs(IPD[18, i] - IPD[17, i]) / 2;
            normparameterbuf[i, 18] = defaultvalue * (diffmean / Prange[i]);
        }
        return normparameterbuf;
    }

}

class Get_ILD : IData_Type
{
    double[,] ILD;
    double[,] _ILD;
    double[] Lrange;
    double[,] normparameterbuf;
    static int flen = 512;
    static int border_n = 750 / (16000 / flen) + 1;

    public Get_ILD()
    {
        ILD = new double[37, flen / 2];
        _ILD = new double[37, flen / 2];
        normparameterbuf = new double[37, flen / 2];
        Lrange = new double[flen / 2];
    }

    public double[,] Read_Data()
    {
        XLWorkbook workbook = new XLWorkbook(string.Format(@".\ILD{0}.xlsx", flen));
        IXLWorksheet worksheet = workbook.Worksheet(1);
        IXLCell cell;
        int lastRow = worksheet.LastRowUsed().RowNumber();
        for (int i = 0; i < 37; i++)
        {
            for (int j = 0; j < flen / 2; j++)
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
        for (int i = border_n; i < flen / 2; i++)
        {
            buf = two_dim.Get_Array("row", i, 37);
            Lrange[i] = buf.Max() - buf.Min();
        }
        return Lrange;
    }

    /// <summary>
    /// No need
    /// </summary>
    /// <param name="border_n"></param>
    /// <param name="_data"></param>
    /// <param name="_datarange"></param>
    /// <returns></returns>
    public double[,] Gain_parameter(int border_n)
    {
        double diffmean;
        double defaultvalue = 500 * 2 * 2;

        Lrange[0] = 0.00001;

        for (int i = border_n; i < 256; i++)
        {
            for (int j = 1; j < 18; j++)
            {
                diffmean = Math.Abs(ILD[j + 1, i] - ILD[j - 1, i]) / 2;
                normparameterbuf[i, j] = defaultvalue * (diffmean / Lrange[i]);
            }
            diffmean = Math.Abs(ILD[1, i] - ILD[0, i]);
            normparameterbuf[i, 0] = defaultvalue * (diffmean / Lrange[i]);
            diffmean = Math.Abs(ILD[18, i] - ILD[17, i]) / 2;
            normparameterbuf[i, 18] = defaultvalue * (diffmean / Lrange[i]);
        }

        return normparameterbuf;
    }
}

/// <summary>
/// This function read the fdbm data
/// </summary>
class CreateReadData
{
    string[] _Path;
    double[,] _data;
    double[] _datarange;
    double[,] _normdata;

    public CreateReadData()
    {
        int flen = 512;
        _Path = new string[] { string.Format(@".\ILD{0}.xlsx", flen), string.Format(@".\IPD{0}.xlsx", flen) };
        //_Path = new string[] { @".\ILD2048.xlsx", @".\IPD2048.xlsx" };
    }

    public void Process(string Type, int border_n, out double[,] _data, out double[] _datarange)
    {
        var r_data = CreateData(Type);
        _data = r_data.Read_Data();
        _datarange = r_data.DataRange();
        //_normdata = r_data.Gain_parameter(border_n); No need
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
    int adelay = 512;
    int frame_shift = 16;
    double forgetting = 0.85;//忘却係数
    int binarymask = 0;
    public int[] Ar_target_beam { set; get; }
    double filternoise = 0.001;
    double attcoef = 1.2;
    int beam_power = 3;
    int onsetmode = 1;
    int FIRfilter = 0;
    int fs = 16000;
    double border_frequency = 125 * 6;

    double forgetting2;
    double border_n;
    double numofloop;
    double normparameter = 100;

    double[] input_win;
    double[] normDbuf;
    double[,] IPD;
    double[] P_range;
    double[,] ILD;
    double[] L_range;

    double[] normparameterbuf;
    double[] normD6;
    double[] normD62;
    public double[] _output { get; set; }
    public double[] output_l { get; set; }
    public double[] output_r { get; set; }

    public Seg()
    {
        Ar_target_beam = new int[10];
        input_win = new double[frame_length];
        forgetting2 = forgetting / (frame_length / frame_shift);
        border_n = border_frequency / (fs / frame_length);
        normparameterbuf = new double[frame_length];
        normD6 = new double[frame_length / 2];
        normD62 = new double[frame_length];
    }

    public void Seg_initial_set_param(int len_l, int len_r)
    {
        numofloop = (len_l - frame_length) / frame_shift + 1;
        normDbuf = new double[frame_length / 2];
        CreateReadData data = new CreateReadData();
        input_win = new Window_function().Hanning_Window(frame_length);
        data.Process("IPD", (int)border_n, out IPD, out P_range);
        data.Process("ILD", (int)border_n, out ILD, out L_range);
        normparameterbuf = Enumerable.Repeat<double>(80, frame_length / 2).ToArray(); //?
        output_l = new double[len_l];
        output_r = new double[len_r];
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


        target_beam /= 5;
        Ar_target_beam[num] = target_beam + 18;
        target_beam = target_beam + 18;

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
                PD[j] = buf.Phase;//Math.Atan(buf.Imaginary/buf.Real);
                LD[j] = 20 * Math.Log10(Complex.Abs(DR[j] + 0.0000001) / Complex.Abs(DL[j] + 0.0000001));
            }

            double[] normD = new double[frame_length / 2];

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
            for (int j = (int)border_n; j < frame_length / 2; j++)
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
            Array.Copy(normDbuf, 0, normD62, 0, frame_length / 2);
            normD62[frame_length / 2] = 0;
            Array.Copy(normDbuf.Reverse().ToArray(), 0, normD62, (frame_length / 2) + 1, (frame_length / 2) - 1);

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
            //MathNet.Numerics.IntegralTransforms.Fourier.Inverse(DL_R, DL_I, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);
            //MathNet.Numerics.IntegralTransforms.Fourier.Inverse(DR_R, DR_I, MathNet.Numerics.IntegralTransforms.FourierOptions.Matlab);

            for (int j = 0; j < frame_length; j++)
            {
                output_l[st2 + j] = output_l[st2 + j] + recovl[j].Real * input_win[j];
                output_r[st2 + j] = output_r[st2 + j] + recovr[j].Real * input_win[j];
                //output_l[st2 + j] = output_l[st2 + j] + DL_R[j] * input_win[j];
                //output_r[st2 + j] = output_r[st2 + j] + DL_I[j] * input_win[j];
            }
        }

        double sigmax = Math.Max(output_l.Max(), output_r.Max()) * 1.05;

        if (target_beam > 18)
        {
            _output = new double[input_r.Length];
            for (int j = 0; j < input_r.Length; j++)
            {
                _output[j] = output_r[j] / sigmax;
            }
        }
        else if (target_beam < 18)
        {
            _output = new double[input_l.Length];
            for (int j = 0; j < input_l.Length; j++)
            {
                _output[j] = output_l[j] / sigmax;
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

}

/// <summary>
/// Hanning window
/// FFT / IFFT
/// </summary>
class Window_function
{
    double[] hanning_window;

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

    static public byte[] Double_to_Byte(double[] buf)
    {
        byte[] _After = new byte[buf.Length * 2];
        byte[] _tmp = new byte[2];
        int i = 0;
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
        Console.Write("IP:");
        string ip = Console.ReadLine();
        var i = Gcs.RecognizeAsync(ip, TimeSpan.FromSeconds(290)).Result;

        /*
        for (int i = 0; flg; i++)
        {
            int _angle = 0;
            flg = int.TryParse(Console.ReadLine(), out _angle);
            if (flg)
            {
                Gcs sentence = new Gcs();
                sentence.transcibe_streaming(50);
            }
            else
            {
                Console.WriteLine("Input data without range");
                Console.ReadLine();
                throw new ArgumentOutOfRangeException();
            }
    }
         */

        Console.ReadLine();
    }
}