﻿using System;
using Matrix.Backends;
namespace Matrix
{
	public class MatrixException : Exception {
		public MatrixException(string message) : base(message){

		}
		public MatrixException(string message,Exception innerException) : base(message,innerException){

		}
	}

	public class MatrixUnsuccessfulConnection : MatrixException{
		public MatrixUnsuccessfulConnection(string message) : base(message){

		}
		public MatrixUnsuccessfulConnection(string message,Exception innerException) : base(message,innerException){
			
		}
	}

	public class MatrixServerError : MatrixException{
		public readonly MatrixErrorCode ErrorCode;
		public readonly string ErrorCodeStr;

		public MatrixServerError (string errorcode, string message) : base(message){
			if (!Enum.TryParse<MatrixErrorCode> (errorcode, out ErrorCode)) {
				ErrorCode = MatrixErrorCode.CL_UNKNOWN_ERROR_CODE;
			}
			ErrorCodeStr = errorcode;
		}

	}

	public class MatrixBadFormatException : MatrixException {
		public MatrixBadFormatException(string value,string type,string reason) : base(String.Format("Value \"{0}\" is not valid for type {1}, Reason: {2}",value,type,reason)){

		}
	}

}