using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace Matrix.Backends
{
	public class TestingAPIBackend : IMatrixAPIBackend
	{
		public MatrixRequestError Get (string apiPath, bool authenticate, out JToken result) {
            result = null;
            return null;
        }

        public Task<MatrixAPIResult> GetAsync(string apiPath, bool authenticate)
            => Task.FromResult<MatrixAPIResult>(default);

		public MatrixRequestError Post (string apiPath, bool authenticate, JToken request, out JToken result) {
            result = null;
            return null;
        }

		public MatrixRequestError Post (string apiPath, bool authenticate, JToken request, Dictionary<string,string> headers, out JToken result) {
            result = null;
            return null;
        }

		public MatrixRequestError Post (string apiPath, bool authenticate, byte[] request , Dictionary<string,string> headers, out JToken result) {
            result = null;
            return null;
        }
        
		public MatrixRequestError Put  (string apiPath, bool authenticate, JToken request, out JToken result) {
            result = null;
            return null;
        }
		
		public Task<MatrixAPIResult> PutAsync  (string apiPath, bool authenticate, JToken request) {
			return null;
		}
		
		public MatrixRequestError Delete  (string apiPath, bool authenticate, out JToken result) {
			result = null;
			return null;
		}

		public void SetAccessToken(string access_token) {

        }
	}
}

