//
// System.Web.Services.Protocols.HttpSoapWebServiceHandler.cs
//
// Author:
//   Lluis Sanchez Gual (lluis@ximian.com)
//
// Copyright (C) Ximian, Inc. 2003
//

using System;
using System.Net;
using System.Web;
using System.Xml;
using System.Text;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

namespace System.Web.Services.Protocols
{
	internal class HttpSoapWebServiceHandler: WebServiceHandler
	{
		SoapTypeStubInfo _typeStubInfo;
		SoapExtension[] _extensionChainHighPrio;
		SoapExtension[] _extensionChainMedPrio;
		SoapExtension[] _extensionChainLowPrio;

		public HttpSoapWebServiceHandler (Type type): base (type)
		{
			_typeStubInfo = (SoapTypeStubInfo) TypeStubManager.GetTypeStub (ServiceType, "Soap");
		}

		public override bool IsReusable 
		{
			get { return false; }
		}

		public override void ProcessRequest (HttpContext context)
		{
			SoapServerMessage requestMessage = null;
			SoapServerMessage responseMessage = null;

			try
			{
				requestMessage = DeserializeRequest (context.Request);
				responseMessage = Invoke (requestMessage);
				SerializeResponse (context.Response, responseMessage);
			}
			catch (Exception ex)
			{
				SerializeFault (context, requestMessage, ex);
				return;
			}
		}

		SoapServerMessage DeserializeRequest (HttpRequest request)
		{
			Stream stream = request.InputStream;

			using (stream)
			{
				string soapAction = null;
				SoapMethodStubInfo methodInfo = null;
				string ctype;
				Encoding encoding = WebServiceHelper.GetContentEncoding (request.ContentType, out ctype);
				if (ctype != "text/xml")
					throw new WebException ("Content is not XML: " + ctype);
					
				object server = CreateServerInstance ();

				SoapServerMessage message = new SoapServerMessage (request, server, stream);
				message.SetStage (SoapMessageStage.BeforeDeserialize);

				// If the routing style is SoapAction, then we can get the method information now
				// and set it to the SoapMessage

				if (_typeStubInfo.RoutingStyle == SoapServiceRoutingStyle.SoapAction)
				{
					soapAction = request.Headers ["SOAPAction"];
					if (soapAction == null) throw new SoapException ("Missing SOAPAction header", SoapException.ClientFaultCode);
					methodInfo = GetMethodFromAction (soapAction);
					message.MethodStubInfo = methodInfo;
				}

				// Execute the high priority global extensions. Do not try to execute the medium and
				// low priority extensions because if the routing style is RequestElement we still
				// don't have method information

				_extensionChainHighPrio = SoapExtension.CreateExtensionChain (_typeStubInfo.SoapExtensions[0]);
				stream = SoapExtension.ExecuteChainStream (_extensionChainHighPrio, stream);
				SoapExtension.ExecuteProcessMessage (_extensionChainHighPrio, message, false);

				// If the routing style is RequestElement, try to get the method name from the
				// stream processed by the high priority extensions

				if (_typeStubInfo.RoutingStyle == SoapServiceRoutingStyle.RequestElement)
				{
					MemoryStream mstream;

					if (stream.CanSeek)
					{
						byte[] buffer = new byte [stream.Length];
						for (int n=0; n<stream.Length;)
							n += stream.Read (buffer, n, (int)stream.Length-n);
						mstream = new MemoryStream (buffer);
					}
					else
					{
						byte[] buffer = new byte [500];
						mstream = new MemoryStream ();
					
						int len;
						while ((len = stream.Read (buffer, 0, 500)) > 0)
							mstream.Write (buffer, 0, len);
						mstream.Position = 0;
					}

					soapAction = ReadActionFromRequestElement (new MemoryStream (mstream.GetBuffer ()), encoding);

					stream = mstream;
					methodInfo = GetMethodFromAction (soapAction);
					message.MethodStubInfo = methodInfo;
				}

				// Whatever routing style we used, we should now have the method information.
				// We can now notify the remaining extensions

				if (methodInfo == null) throw new SoapException ("Method '" + soapAction + "' not defined in the web service '" + _typeStubInfo.LogicalType.WebServiceName + "'", SoapException.ClientFaultCode);

				_extensionChainMedPrio = SoapExtension.CreateExtensionChain (methodInfo.SoapExtensions);
				_extensionChainLowPrio = SoapExtension.CreateExtensionChain (_typeStubInfo.SoapExtensions[1]);

				stream = SoapExtension.ExecuteChainStream (_extensionChainMedPrio, stream);
				stream = SoapExtension.ExecuteChainStream (_extensionChainLowPrio, stream);
				SoapExtension.ExecuteProcessMessage (_extensionChainMedPrio, message, false);
				SoapExtension.ExecuteProcessMessage (_extensionChainLowPrio, message, false);

				// Deserialize the request

				StreamReader reader = new StreamReader (stream, encoding, false);
				XmlTextReader xmlReader = new XmlTextReader (reader);

				try
				{
					object content;
					SoapHeaderCollection headers;
					WebServiceHelper.ReadSoapMessage (xmlReader, _typeStubInfo, methodInfo.Use, methodInfo.RequestSerializer, out content, out headers);
					message.InParameters = (object []) content;
					message.SetHeaders (headers);
				}
				catch (Exception ex)
				{
					Console.WriteLine (ex);
					throw new SoapException ("Could not deserialize Soap message", SoapException.ClientFaultCode, ex);
				}

				// Notify the extensions after deserialization

				message.SetStage (SoapMessageStage.AfterDeserialize);
				SoapExtension.ExecuteProcessMessage (_extensionChainHighPrio, message, false);
				SoapExtension.ExecuteProcessMessage (_extensionChainMedPrio, message, false);
				SoapExtension.ExecuteProcessMessage (_extensionChainLowPrio, message, false);

				xmlReader.Close ();

				return message;
			}
		}

		SoapMethodStubInfo GetMethodFromAction (string soapAction)
		{
			soapAction = soapAction.Trim ('"',' ');
			int i = soapAction.LastIndexOf ('/');
			string methodName = soapAction.Substring (i + 1);
			return (SoapMethodStubInfo) _typeStubInfo.GetMethod (methodName);
		}

		string ReadActionFromRequestElement (Stream stream, Encoding encoding)
		{
			try
			{
				StreamReader reader = new StreamReader (stream, encoding, false);
				XmlTextReader xmlReader = new XmlTextReader (reader);

				xmlReader.MoveToContent ();
				xmlReader.ReadStartElement ("Envelope", WebServiceHelper.SoapEnvelopeNamespace);

				while (! (xmlReader.NodeType == XmlNodeType.Element && xmlReader.LocalName == "Body" && xmlReader.NamespaceURI == WebServiceHelper.SoapEnvelopeNamespace))
					xmlReader.Skip ();

				xmlReader.ReadStartElement ("Body", WebServiceHelper.SoapEnvelopeNamespace);
				xmlReader.MoveToContent ();

				if (xmlReader.NamespaceURI.EndsWith ("/")) return xmlReader.NamespaceURI + xmlReader.LocalName;
				else return xmlReader.NamespaceURI + "/" + xmlReader.LocalName;
			}
			catch (Exception ex)
			{
				string errmsg = "The root element for the request could not be determined. ";
				errmsg += "When RoutingStyle is set to RequestElement, SoapExtensions configured ";
				errmsg += "via an attribute on the method cannot modify the request stream before it is read. ";
				errmsg += "The extension must be configured via the SoapExtensionTypes element in web.config ";
				errmsg += "or the request must arrive at the server as clear text.";
				throw new SoapException (errmsg, SoapException.ServerFaultCode, ex);
			}
		}

		void SerializeResponse (HttpResponse response, SoapServerMessage message)
		{
			SoapMethodStubInfo methodInfo = message.MethodStubInfo;
			
			response.ContentType = "text/xml; charset=utf-8";
			if (message.Exception != null) response.StatusCode = 500;

			long contentLength = 0;
			Stream outStream = null;
			MemoryStream bufferStream = null;
			Stream responseStream = response.OutputStream;
			
			if (methodInfo.MethodAttribute.BufferResponse)
			{
				bufferStream = new MemoryStream ();
				outStream = bufferStream;
			}
			else
				outStream = responseStream;

			try
			{
				// While serializing, process extensions in reverse order

				if (methodInfo.MethodAttribute.BufferResponse)
				{
					outStream = SoapExtension.ExecuteChainStream (_extensionChainHighPrio, outStream);
					outStream = SoapExtension.ExecuteChainStream (_extensionChainMedPrio, outStream);
					outStream = SoapExtension.ExecuteChainStream (_extensionChainLowPrio, outStream);
	
					message.SetStage (SoapMessageStage.BeforeSerialize);
					SoapExtension.ExecuteProcessMessage (_extensionChainLowPrio, message, true);
					SoapExtension.ExecuteProcessMessage (_extensionChainMedPrio, message, true);
					SoapExtension.ExecuteProcessMessage (_extensionChainHighPrio, message, true);
				}
				
				// What a waste of UTF8encoders, but it has to be thread safe.
				XmlTextWriter xtw = new XmlTextWriter (outStream, new UTF8Encoding (false));
				xtw.Formatting = Formatting.Indented;	// TODO: remove formatting when code is stable
				System.Web.Services.Description.SoapBindingUse use = message.MethodStubInfo.Use;

				if (message.Exception == null)
					WebServiceHelper.WriteSoapMessage (xtw, _typeStubInfo, use, message.MethodStubInfo.ResponseSerializer, message.OutParameters, message.Headers);
				else
					WebServiceHelper.WriteSoapMessage (xtw, _typeStubInfo, use, _typeStubInfo.GetFaultSerializer(use), new Fault (message.Exception), null);

				if (methodInfo.MethodAttribute.BufferResponse)
				{
					message.SetStage (SoapMessageStage.AfterSerialize);
					SoapExtension.ExecuteProcessMessage (_extensionChainLowPrio, message, true);
					SoapExtension.ExecuteProcessMessage (_extensionChainMedPrio, message, true);
					SoapExtension.ExecuteProcessMessage (_extensionChainHighPrio, message, true);
				}
				
				if (bufferStream != null) contentLength = bufferStream.Length;
				xtw.Close ();
			}
			catch (Exception ex)
			{
				// If the response is buffered, we can discard the response and
				// serialize a new Fault message as response.
				if (methodInfo.MethodAttribute.BufferResponse) throw ex;
				
				// If it is not buffered, we can't rollback what has been sent,
				// so we can only close the connection and return.
				responseStream.Close ();
				return;
			}
			
			try
			{
				if (methodInfo.MethodAttribute.BufferResponse)
					responseStream.Write (bufferStream.GetBuffer(), 0, (int) contentLength);
			}
			catch {}
			
			responseStream.Close ();
		}

		void SerializeFault (HttpContext context, SoapServerMessage requestMessage, Exception ex)
		{
			SoapException soex = ex as SoapException;
			if (soex == null) soex = new SoapException (ex.Message, SoapException.ServerFaultCode, ex);

			MethodStubInfo stubInfo;
			object server;
			Stream stream;

			SoapServerMessage faultMessage;
			if (requestMessage != null)
				faultMessage = new SoapServerMessage (context.Request, soex, requestMessage.MethodStubInfo, requestMessage.Server, requestMessage.Stream);
			else
				faultMessage = new SoapServerMessage (context.Request, soex, null, null, null);

			SerializeResponse (context.Response, faultMessage);
			return;
		}
		
		private SoapServerMessage Invoke (SoapServerMessage requestMessage)
		{
			SoapMethodStubInfo methodInfo = requestMessage.MethodStubInfo;

			// Assign header values to web service members

			requestMessage.UpdateHeaderValues (requestMessage.Server, methodInfo.Headers);

			// Fill an array with the input parameters at the right position

			object[] parameters = new object[methodInfo.MethodInfo.Parameters.Length];
			ParameterInfo[] inParams = methodInfo.MethodInfo.InParameters;
			for (int n=0; n<inParams.Length; n++)
				parameters [inParams[n].Position] = requestMessage.InParameters [n];

			// Invoke the method

			try
			{
				object[] results = methodInfo.MethodInfo.Invoke (requestMessage.Server, parameters);
				requestMessage.OutParameters = results;
			}
			catch (TargetInvocationException ex)
			{
				throw ex.InnerException;
			}

			// Check that headers with MustUnderstand flag have been understood
			
			foreach (SoapHeader header in requestMessage.Headers)
			{
				if (header.MustUnderstand && !header.DidUnderstand)
					throw new SoapHeaderException ("Header not understood: " + header.GetType(), SoapException.MustUnderstandFaultCode);
			}

			// Collect headers that must be sent to the client
			requestMessage.CollectHeaders (requestMessage.Server, methodInfo.Headers, SoapHeaderDirection.Out);

			return requestMessage;
		}
	}
}
