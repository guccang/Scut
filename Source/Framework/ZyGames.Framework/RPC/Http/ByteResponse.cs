﻿/****************************************************************************
Copyright (c) 2013-2015 scutgame.com

http://www.scutgame.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
****************************************************************************/

using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

#pragma warning disable 1998

namespace ZyGames.Framework.RPC.Http
{
    /// <summary>
    /// 
    /// </summary>
    public class ByteResponse : StatusResponse, IHttpResponseAction
    {
        readonly byte[] data;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="statusCode"></param>
        /// <param name="statusDescription"></param>
        /// <param name="value"></param>
        /// <param name="identity"></param>
        public ByteResponse(int statusCode, string statusDescription, byte[] value, string identity = null)
            : base(statusCode, statusDescription, identity)
        {
            data = value;
        }

        /// <summary>
        /// 
        /// </summary>
        public event Action<IHttpRequestResponseContext> CookieHandle;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        protected virtual void SetCookie(IHttpRequestResponseContext context)
        {
            Action<IHttpRequestResponseContext> handler = CookieHandle;
            if (handler != null) handler(context);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override async Task Execute(IHttpRequestResponseContext context)
        {
            bool isProcessPay = context.Request.RawUrl.IndexOf("PayBaiDu") > 0;
            if (isProcessPay)
            {
                string info = System.Text.Encoding.UTF8.GetString(data);
                context.Response.ContentType = "text/json";
                context.Response.StatusCode = 200;
                context.Response.StatusDescription = "ok";
                using (Stream output = context.Response.OutputStream)
                {
                    StreamWriter myStreamWriter = new StreamWriter(output, System.Text.Encoding.UTF8);
                    await myStreamWriter.WriteAsync(info);
                    myStreamWriter.Close();
                }
                return;
            }
            else if(context.Request.RawUrl.IndexOf("Pay360") > 0)
            {
                context.Response.ContentType = "text/html";
                context.Response.StatusCode = 200;
                context.Response.StatusDescription = "ok";
                byte[] TheData = System.Text.Encoding.UTF8.GetBytes("ok");
                context.Response.ContentLength64 = TheData.Length;
                context.Response.ContentEncoding = System.Text.Encoding.UTF8;
                using (Stream output = context.Response.OutputStream)
                {
                    await output.WriteAsync(TheData, 0, TheData.Length);
                    output.Close();
                }
                return;
            }
            else if (context.Request.RawUrl.IndexOf("Guard") > 0)
            {
                context.Response.ContentType = "text/html";
                context.Response.StatusCode = 200;
                context.Response.StatusDescription = "ok";
                byte[] TheData = System.Text.Encoding.UTF8.GetBytes("guard ok");
                context.Response.ContentLength64 = TheData.Length;
                context.Response.ContentEncoding = System.Text.Encoding.UTF8;
                using (Stream output = context.Response.OutputStream)
                {
                    await output.WriteAsync(TheData, 0, TheData.Length);
                    output.Close();
                }
                return;
            }
            else if(context.Request.RawUrl.IndexOf("webBackOffice")>0)
            {
                context.Response.ContentType = "text/html";
                context.Response.StatusCode = 200;
                context.Response.StatusDescription = "ok";
                byte[] TheData = data;
                context.Response.ContentLength64 = TheData.Length;
                context.Response.ContentEncoding = System.Text.Encoding.UTF8;
                using (Stream output = context.Response.OutputStream)
                {
                    await output.WriteAsync(TheData, 0, TheData.Length);
                    output.Close();
                }
                return;
            }
            else if (data == null)
            {
                context.Response.StatusCode = 500;
                return;
            }

            SetStatus(context);
            SetCookie(context);
            //js call need
            context.Response.AddHeader("Access-Control-Allow-Origin", "*");
            if (context.Request.QueryString["showjson"] == "1")
            {
                context.Response.ContentType = "text/json";
                context.Response.SendChunked = false;
                context.Response.ContentLength64 = data.Length;
                using (Stream output = context.Response.OutputStream)
                {
                    await output.WriteAsync(data, 0, data.Length);
                    output.Close();
                }
            }
            else
            {
                context.Response.ContentType = "application/octet-stream";
                context.Response.SendChunked = false;
                int offset = 0;
                if (data.Length > 3 && data[offset] == 0x1f && data[offset + 1] == 0x8b && data[offset + 2] == 0x08 && data[offset + 3] == 0x00)
                {
                    context.Response.AddHeader("Content-Encoding", "gzip");
                }

                context.Response.ContentLength64 = data.Length;
                using (Stream output = context.Response.OutputStream)
                {
                    await output.WriteAsync(data, 0, data.Length);
                    output.Close();
                }
            }
        }
    }
}
