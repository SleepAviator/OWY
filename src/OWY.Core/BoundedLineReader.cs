using System.Text;

namespace OWY.Core;

/// <summary>Bounded line framing with a reusable block buffer; preserves excess input for the next frame.</summary>
public sealed class BoundedLineReader(StreamReader reader,int bound=Protocol.MaxFrameBytes)
{
    readonly Stream stream=reader.BaseStream;
    readonly byte[] bytes=new byte[4096];readonly char[] buffer=new char[4098];
    readonly Decoder decoder=new UTF8Encoding(false,true).GetDecoder();int offset,length;
    public async Task<string?> Read(CancellationToken token)
    {
        var line=new StringBuilder();
        while(true)
        {
            if(offset==length)
            {
                // StreamReader.ReadAsync can wait to fill its requested character
                // count. Reading available pipe bytes keeps each one-second frame immediate.
                int count=await stream.ReadAsync(bytes.AsMemory(),token);offset=0;
                try{length=decoder.GetChars(bytes,0,count,buffer,0,count==0);}
                catch(DecoderFallbackException ex){throw new InvalidDataException("IPC contains invalid UTF-8",ex);}
                if(count==0){if(line.Length>0)throw new EndOfStreamException("Interrupted IPC frame");return null;}
            }
            int end=Array.IndexOf(buffer,'\n',offset,length-offset);
            int take=(end>=0?end:length)-offset;
            if(line.Length+take>bound)throw new InvalidDataException("IPC line exceeded its bound");
            line.Append(buffer,offset,take);offset+=take;
            if(end>=0){offset++;return line.ToString().TrimEnd('\r');}
        }
    }
}
