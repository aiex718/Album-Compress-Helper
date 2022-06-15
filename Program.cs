using System.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommandLine;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;

namespace Album_Compress_Helper
{
    class Program
    {
        const string input_identifier = "%in%";
        const string output_identifier = "%out%";

        public class Options
        {
            [Option("src", Required = true, HelpText = "Path to source directory.")]
            public string Source { get; set; }

            [Option("dst", Required = true, HelpText = "Path to destination directory, create if not exist.")]
            public string Destination { get; set; }

            [Option("argff", Required = false, HelpText = "Arguments pass to FFMPEG, use "+input_identifier+" for input file path, "+output_identifier+" for output file path")]
            public string FFArgument { get; set; }

            [Option("argexif", Required = false, HelpText = "Arguments pass to ExifTool, use "+input_identifier+" for input file path, "+output_identifier+" for output file path")]
            public string ExifArgument { get; set; }

            [Option('c',"comment", Required = false, HelpText = "Ignore file with spicific comment tag from input, otherwise write comment to output file.")]
            public string Comment { get; set; }

            [Option('i',"ignore", Required = false, HelpText = "Ignore if files already exist in destination.")]
            public bool Ignore { get; set; }

            [Option("ext",Separator = ',', Required = true, HelpText = "File extensions filter such as jpg or mp4, use',' to seperate multiple extensions.")]
            public IEnumerable<string> Extensions { get; set; }
            
            [Option('t', "thread", Required = false, HelpText = ".",Default = 1)]
            public int Thread { get; set; }

            [Option('d', "date", Required = false, HelpText = "Modify last write and creation time, available options:\n copy:copy from original \n min or max: select minimum or maximum datetime, then write to both last write and creation time.")]
            public string Date { get; set; }

            [Option('k', "keep", Required = false, HelpText = "Copy original file if compressed version is larger.")]
            public bool Keep { get; set; }

            [Option('v', "verbose", Required = false, HelpText = "Show verbose info.")]
            public bool Verbose { get; set; }
            
            [Option("vvv", Required = false, HelpText = "Show more verbose info, mostly from ffmpeg.")]
            public bool VerboseMore { get; set; }
        }

        public static Options Option {get;set;}

        static async Task Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(o=>Option=o)
                .WithNotParsed(o=>Environment.Exit(-1));

            Option.FFArgument = Option.FFArgument?.Replace("\\","");
            Option.ExifArgument = Option.ExifArgument?.Replace("\\","");

            List<string> ExtensionFilters = Option.Extensions.ToList();

            var FilePaths = Directory.GetFiles(Option.Source, "*", SearchOption.AllDirectories)
                .Where(f=>ExtensionFilters.Any(e=>f.EndsWith(e,StringComparison.CurrentCultureIgnoreCase)))
                .ToList();

            int Process_cnt=0,Ignored_cnt=0,Keep_cnt=0;
            var TaskList = new List<Task>();

            foreach (var FilePath in FilePaths)
            {
                var SavePath = FilePath.Replace(Option.Source,Option.Destination);

                //multi thread
                if (TaskList.Count>=Option.Thread)
                {
                    Task FinishedTask = await Task.WhenAny(TaskList);
                    TaskList.Remove(FinishedTask);
                    Interlocked.Increment(ref Process_cnt);
                }

                //Check Ignore
                if(Option.Ignore && File.Exists(SavePath))
                {
                    Console.WriteLine($"File exist, ignored:{SavePath}");
                    Interlocked.Increment(ref Ignored_cnt);
                    continue;//skip   
                }

                Directory.CreateDirectory(Path.GetDirectoryName(SavePath));                
                
                Task t = Task.Run(()=>{
                    
                    //Check Comment
                    if(String.IsNullOrEmpty(Option.Comment)==false && 
                        Option.Comment == ReadComment(FilePath))
                    {
                        Console.WriteLine($"File already has comment, ignored:{SavePath}");
                        Interlocked.Increment(ref Ignored_cnt);
                        return;//skip
                    }

                    if(String.IsNullOrEmpty(Option.FFArgument)==false)
                        RunFFMPEG(FilePath,SavePath);

                    if(String.IsNullOrEmpty(Option.ExifArgument)==false)
                        RunExifTool(FilePath,SavePath);

                    if(String.IsNullOrEmpty(Option.Date)==false)
                        CopyDate(FilePath,SavePath);

                    if(Option.Keep && CheckFileSize(FilePath,SavePath))
                    {
                        Console.WriteLine($"Output file is larger, copy original:{SavePath}");
                        Interlocked.Increment(ref Keep_cnt);
                    }
                        
                });

                TaskList.Add(t);
                Console.WriteLine($"Processing:{SavePath}");
                WriteOnBottomLine($"File Count:{FilePaths.Count}, Processed:{Process_cnt.ToString()}, Ignored:{Ignored_cnt.ToString()}, Keep:{Keep_cnt.ToString()}");
            }


            while(TaskList.Count!=0)
            {
                Task FinishedTask = await Task.WhenAny(TaskList);
                TaskList.Remove(FinishedTask);
                Interlocked.Increment(ref Process_cnt);
            }
            WriteOnBottomLine($"File Count:{FilePaths.Count}, Processed:{Process_cnt.ToString()}, Ignored:{Ignored_cnt.ToString()}, Keep:{Keep_cnt.ToString()}");
        }
        static string ReadComment(string FilePath)
        {
            string comment=null;

            var Info = new ProcessStartInfo
            {
                FileName = @"exiftool",
                Arguments = $"-comment {FilePath}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            
            using(Process proc = Process.Start(Info))                        
            {
                var str = proc.StandardOutput.ReadToEnd();
                if(String.IsNullOrEmpty(str)==false)
                {                    
                    var ary = str.Split(':').ToArray();
                    if(ary.Length>1)
                        comment = ary[1].Replace("\n", "")
                        .Replace("\r", "")
                        .TrimStart()
                        .TrimEnd();
                }
                proc.WaitForExit(); 
            }

            return comment;
        }

        static void RunFFMPEG(string FilePath,string SavePath)
        {
            string arg = new string(Option.FFArgument)
                .Replace(input_identifier,$"\"{FilePath}\"")
                .Replace(output_identifier,$"\"{SavePath}\"");

            ProcessStartInfo ff_procinfo = new ProcessStartInfo()
            {
                FileName = "ffmpeg",
                Arguments= arg,
                UseShellExecute = false,
                RedirectStandardOutput = true,                
                CreateNoWindow = !Option.VerboseMore
            };

            if(Option.Verbose)
                Console.WriteLine($"{ff_procinfo.FileName} {ff_procinfo.Arguments}");
            using(Process proc = Process.Start(ff_procinfo))                        
                proc.WaitForExit();     
        }

        static void RunExifTool(string FilePath,string SavePath)
        {
            string arg = new string(Option.ExifArgument)
                .Replace(input_identifier,$"\"{FilePath}\"")
                .Replace(output_identifier,$"\"{SavePath}\"");

            if(String.IsNullOrEmpty(Option.Comment)==false)
                arg+=$" -comment={Option.Comment} -usercomment={Option.Comment}";

            ProcessStartInfo exif_procinfo = new ProcessStartInfo()
            {
                FileName = "exiftool",
                Arguments=arg,
                UseShellExecute = false,
                RedirectStandardOutput = true,                
                CreateNoWindow = !Option.VerboseMore
            };
            if(Option.Verbose)
                Console.WriteLine($"{exif_procinfo.FileName} {exif_procinfo.Arguments}");
            using(Process proc = Process.Start(exif_procinfo))
                proc.WaitForExit();
        }

        static void CopyDate(string FilePath,string SavePath)
        {
            var CreationTime = File.GetCreationTime(FilePath);
            var LastWriteTime = File.GetLastWriteTime(FilePath);
            var Min = CreationTime>LastWriteTime?LastWriteTime:CreationTime;
            var Max = CreationTime<LastWriteTime?LastWriteTime:CreationTime;

            switch(Option.Date)
            {
                case "copy":
                    File.SetCreationTime(SavePath,CreationTime);
                    File.SetLastWriteTime(SavePath,LastWriteTime);    
                break;

                case "min":
                    File.SetCreationTime(SavePath,Min);
                    File.SetLastWriteTime(SavePath,Min);    
                break;

                case "max":
                    File.SetCreationTime(SavePath,Max);
                    File.SetLastWriteTime(SavePath,Max);    
                break;
            }
        }
        static bool CheckFileSize(string FilePath,string SavePath)
        {
            if(File.Exists(FilePath) && File.Exists(SavePath))
            {
                var file_length = new FileInfo(FilePath).Length;
                var save_length = new FileInfo(SavePath).Length;
                if (save_length>file_length)
                {
                    File.Copy(FilePath, SavePath, true);
                    return true;
                }
            }

            return false;
        }

        static void WriteOnBottomLine(string text)
        {
            int x = Console.CursorLeft;
            int y = Console.CursorTop;
            Console.CursorTop = Console.WindowTop + Console.WindowHeight - 1;
            Console.Write(text);
            Console.SetCursorPosition(x, y);
        }
    }
}

