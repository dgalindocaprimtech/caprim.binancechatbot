// Necesitarás estos paquetes NuGet (puedes añadirlos con `dotnet add package ...`):
// System.Net.Http.Json (si no está ya por defecto con .NET 8 SDK) System.Text.Json (normalmente incluido)

using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Globalization;
using System.Net.Http.Headers;

// using System.Net.Http.Json; // No se usa directamente en este snippet, pero útil en general
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web; // Para HttpUtility.UrlEncode

public class BinanceC2CChatClient
{
    private const string BASE_URL = "https://api.binance.com";

    // --- IMPORTANTE: Estas son tus credenciales. Ten cuidado al compartirlas. ---
    public static string KEY = string.Empty;

    public static string SECRET = string.Empty;
    public static string FormGoogle = string.Empty;
    public static string PostgresDb = string.Empty;
    public static Int32 Level1;
    public static Int32 Level2;
    // --- --- --- --- --- --- --- --- --- --- --- --- --- --- --- --- --- --- ---

    private static readonly HttpClient httpClient;

    static BinanceC2CChatClient()
    {
        httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var builder = new ConfigurationBuilder()
              .SetBasePath(Directory.GetCurrentDirectory()) // Establece la ruta base al directorio actual
              .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false); // Agrega appsettings.json

        // 2. Construir la configuración
        IConfigurationRoot configuration = builder.Build();
        if (configuration != null)
        {
            KEY = configuration["BinanceApi:ApiKey"].ToString(); // Asumiendo que tienes una clave llamada "ClaveEjemplo" en appsettings.json
            SECRET = configuration["BinanceApi:SecretKey"].ToString(); // Asumiendo que tienes una clave llamada "ClaveEjemplo" en appsettings.json
            FormGoogle = configuration["BinanceApi:FormGoogle"].ToString();
            PostgresDb = configuration["ConnectionStrings:PostgresDb"].ToString();
            Level1 = (Int32.TryParse(configuration["Levels:Level1"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 monto) ? monto : 0);
            Level2 = (Int32.TryParse(configuration["Levels:Level2"].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 monto2) ? monto2 : 0);
        }
        httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", KEY);
        httpClient.DefaultRequestHeaders.Add("clientType", "WEB");
    }

    private static string HmacSha256(string data, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        using (var hmac = new HMACSHA256(keyBytes))
        {
            var hashBytes = hmac.ComputeHash(dataBytes);
            var sb = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }

    private static long GetTimestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string BuildQueryString(Dictionary<string, object> parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return string.Empty;
        }
        var queryBuilder = new StringBuilder();
        foreach (var param in parameters)
        {
            if (queryBuilder.Length > 0)
            {
                queryBuilder.Append("&");
            }
            queryBuilder.Append($"{HttpUtility.UrlEncode(param.Key)}={HttpUtility.UrlEncode(param.Value.ToString())}");
        }
        return queryBuilder.ToString(); // .Replace("%27", "%22"); // Comentado como en tu código
    }

    private static async Task<TResponse?> SendSignedRequestAsync<TResponse>(
        HttpMethod httpMethod,
        string urlPath,
        Dictionary<string, object>? payload = null, // Parámetros para la query string (y firma)
        object? dataLoad = null) where TResponse : class // Objeto para el cuerpo (body) de la petición
    {
        payload ??= new Dictionary<string, object>();
        string queryString = BuildQueryString(payload);

        if (!string.IsNullOrEmpty(queryString))
        {
            queryString = $"{queryString}&timestamp={GetTimestamp()}";
        }
        else
        {
            queryString = $"timestamp={GetTimestamp()}";
        }

        string signature = HmacSha256(queryString, SECRET);
        string fullUrl = $"{urlPath}?{queryString}&signature={signature}";

        // Console.WriteLine($"DEBUG HTTP Request: {httpMethod} {fullUrl}");
        //if (dataLoad != null)
        //{
        //    Console.WriteLine($"DEBUG HTTP Body: {JsonSerializer.Serialize(dataLoad)}");
        //}

        var request = new HttpRequestMessage(httpMethod, fullUrl);

        if (dataLoad != null && (httpMethod == HttpMethod.Post || httpMethod == HttpMethod.Put))
        {
            string jsonBody = JsonSerializer.Serialize(dataLoad);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        // Mueve la declaración de responseString aquí para que sea accesible en el catch de JsonException
        string? responseString = null;
        try
        {
            HttpResponseMessage response = await httpClient.SendAsync(request);
            responseString = await response.Content.ReadAsStringAsync(); // Asigna a la variable de alcance superior

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Request error (Status {response.StatusCode}): {responseString}");
                try
                {
                    var errorResponse = JsonSerializer.Deserialize<BinanceErrorResponse>(responseString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    Console.WriteLine($"Binance API Error: Code={errorResponse?.Code}, Msg={errorResponse?.Msg}");
                }
                catch { /* No es un error JSON de Binance o no se pudo deserializar */ }
                return null;
            }

            return JsonSerializer.Deserialize<TResponse>(responseString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"HttpRequestException: {e.Message}");
            if (e.InnerException != null) Console.WriteLine($"Inner error: {e.InnerException.Message}");
            return null;
        }
        catch (JsonException e) // Ahora responseString es accesible aquí
        {
            // Esta es la línea corregida (antes era la 145 aproximadamente)
            Console.WriteLine($"JSON deserialization error: {e.Message}. Raw string that failed to parse: {responseString ?? "Response string was null or unreadable."}");
            return null;
        }
    }

    // Clase genérica para errores de Binance
    public class BinanceErrorResponse
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("msg")]
        public string? Msg { get; set; }
    }

    // --- Clases para /sapi/v1/c2c/chat/retrieveChatCredential ---
    public class ChatCredentialData
    {
        [JsonPropertyName("chatWssUrl")]
        public string? ChatWssUrl { get; set; }

        [JsonPropertyName("listenKey")]
        public string? ListenKey { get; set; }

        [JsonPropertyName("listenToken")]
        public string? ListenToken { get; set; }
    }

    public class ApiChatCredentialResponse
    {
        [JsonPropertyName("data")]
        public ChatCredentialData? Data { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public class ApiAllChatMessagesWithPaginationResponse
    {
        [JsonPropertyName("data")]
        public List<AllChatMessagesData>? Data { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("total")]
        public int? Total { get; set; }
    }

    public class AllChatMessagesData
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("createTime")]
        public long CreateTime { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        // Este campo se mapeará desde el "orderNo" en el JSON.
        [JsonPropertyName("orderNo")]
        public string? OrderNo { get; set; }

        [JsonPropertyName("self")]
        public bool Self { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; } // En tu ejemplo, este es un UUID estándar con guiones

        [JsonPropertyName("fromNickName")]
        public string? FromNickName { get; set; } // En tu ejemplo, este es un UUID estándar con guiones
    }

    public class WebSocketMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("createTime")]
        public long CreateTime { get; set; }

        [JsonPropertyName("groupId")]
        public string? GroupId { get; set; }

        [JsonPropertyName("hasOngoingOrder")]
        public bool HasOngoingOrder { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        // Este campo se mapeará desde el "orderNo" en el JSON.
        [JsonPropertyName("orderNo")]
        public string? OrderNo { get; set; }

        [JsonPropertyName("self")]
        public bool Self { get; set; }

        public bool isBuyer { get; set; }

        [JsonPropertyName("sourceType")]
        public string? SourceType { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("topicId")]
        public string? TopicId { get; set; } // Este ya estaba y coincide con orderNo en tu ejemplo

        [JsonPropertyName("topicType")]
        public string? TopicType { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("unreadCount")]
        public int UnreadCount { get; set; }

        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; } // En tu ejemplo, este es un UUID estándar con guiones

        [JsonPropertyName("fromNickname")]
        public string? FromNickname { get; set; }
    }

    public class ChatMessagePayload
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "text";

        [JsonPropertyName("uuid")]
        public string Uuid { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("orderNo")]
        public string? OrderNo { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = "Hello, this is a test message from .NET!";

        [JsonPropertyName("self")]
        public bool Self { get; set; } = true;

        [JsonPropertyName("clientType")]
        public string ClientType { get; set; } = "web";

        [JsonPropertyName("createTime")]
        public long CreateTime { get; set; } = GetTimestamp();

        [JsonPropertyName("sendStatus")]
        public int SendStatus { get; set; } = 0;
    }

    // --- Clases para /sapi/v1/c2c/orderMatch/getUserOrderDetail (Respuesta) ---
    public class FieldDetail
    {
        [JsonPropertyName("fieldId")]
        public string? FieldId { get; set; }

        [JsonPropertyName("fieldName")]
        public string? FieldName { get; set; }

        [JsonPropertyName("fieldContentType")]
        public string? FieldContentType { get; set; }

        [JsonPropertyName("restrictionType")]
        public int RestrictionType { get; set; }

        [JsonPropertyName("lengthLimit")]
        public int LengthLimit { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("isCopyable")]
        public bool IsCopyable { get; set; }

        [JsonPropertyName("hintWord")]
        public string? HintWord { get; set; }

        [JsonPropertyName("fieldValue")]
        public string? FieldValue { get; set; }
    }

    public class PayMethod
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("identifier")]
        public string? Identifier { get; set; }

        [JsonPropertyName("tradeMethodName")]
        public string? TradeMethodName { get; set; }

        [JsonPropertyName("fields")]
        public List<FieldDetail>? Fields { get; set; }

        [JsonPropertyName("iconUrlColor")]
        public string? IconUrlColor { get; set; }
    }

    public class TradeMethodCommissionRateVo // Definición vacía según tu ejemplo
    { }

    public class OrderDetailData
    {
        [JsonPropertyName("orderNumber")] public string? OrderNumber { get; set; }
        [JsonPropertyName("advOrderNumber")] public string? AdvOrderNumber { get; set; }
        [JsonPropertyName("buyerMobilePhone")] public string? BuyerMobilePhone { get; set; }
        [JsonPropertyName("sellerMobilePhone")] public string? SellerMobilePhone { get; set; }
        [JsonPropertyName("buyerNickname")] public string? BuyerNickname { get; set; }
        [JsonPropertyName("buyerName")] public string? BuyerName { get; set; }
        [JsonPropertyName("sellerNickname")] public string? SellerNickname { get; set; }
        [JsonPropertyName("sellerName")] public string? SellerName { get; set; }
        [JsonPropertyName("tradeType")] public string? TradeType { get; set; }
        [JsonPropertyName("payType")] public string? PayType { get; set; }
        [JsonPropertyName("payMethods")] public List<PayMethod>? PayMethods { get; set; }
        [JsonPropertyName("selectedPayId")] public long SelectedPayId { get; set; }
        [JsonPropertyName("orderStatus")] public int OrderStatus { get; set; }
        [JsonPropertyName("asset")] public string? Asset { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("price")] public string? Price { get; set; }
        [JsonPropertyName("totalPrice")] public string? TotalPrice { get; set; }
        [JsonPropertyName("fiatUnit")] public string? FiatUnit { get; set; }
        [JsonPropertyName("isComplaintAllowed")] public bool IsComplaintAllowed { get; set; }
        [JsonPropertyName("confirmPayTimeout")] public int ConfirmPayTimeout { get; set; }
        [JsonPropertyName("remark")] public string? Remark { get; set; }
        [JsonPropertyName("createTime")] public long CreateTime { get; set; }
        [JsonPropertyName("notifyPayTime")] public long? NotifyPayTime { get; set; }
        [JsonPropertyName("confirmPayTime")] public long? ConfirmPayTime { get; set; }
        [JsonPropertyName("notifyPayEndTime")] public long? NotifyPayEndTime { get; set; }
        [JsonPropertyName("confirmPayEndTime")] public long? ConfirmPayEndTime { get; set; }
        [JsonPropertyName("fiatSymbol")] public string? FiatSymbol { get; set; }
        [JsonPropertyName("currencyTicketSize")] public string? CurrencyTicketSize { get; set; }
        [JsonPropertyName("assetTicketSize")] public string? AssetTicketSize { get; set; }
        [JsonPropertyName("priceTicketSize")] public string? PriceTicketSize { get; set; }
        [JsonPropertyName("notifyPayedExpireMinute")] public int NotifyPayedExpireMinute { get; set; }
        [JsonPropertyName("confirmPayedExpireMinute")] public int ConfirmPayedExpireMinute { get; set; }
        [JsonPropertyName("clientType")] public string? ClientType { get; set; }
        [JsonPropertyName("onlineStatus")] public string? OnlineStatus { get; set; }
        [JsonPropertyName("merchantNo")] public string? MerchantNo { get; set; }
        [JsonPropertyName("origin")] public string? Origin { get; set; }
        [JsonPropertyName("unreadCount")] public int? UnreadCount { get; set; } // Anulable por si no viene
        [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
        [JsonPropertyName("avgReleasePeriod")] public int AvgReleasePeriod { get; set; }
        [JsonPropertyName("avgPayPeriod")] public int AvgPayPeriod { get; set; }
        [JsonPropertyName("expectedPayTime")] public long ExpectedPayTime { get; set; }
        [JsonPropertyName("expectedReleaseTime")] public long ExpectedReleaseTime { get; set; }
        [JsonPropertyName("commissionRate")] public string? CommissionRate { get; set; }
        [JsonPropertyName("commission")] public string? Commission { get; set; }
        [JsonPropertyName("takerCommissionRate")] public string? TakerCommissionRate { get; set; }
        [JsonPropertyName("takerCommission")] public string? TakerCommission { get; set; }
        [JsonPropertyName("takerAmount")] public string? TakerAmount { get; set; }
        [JsonPropertyName("tradeMethodCommissionRateVoList")] public List<TradeMethodCommissionRateVo>? TradeMethodCommissionRateVoList { get; set; }
        [JsonPropertyName("additionalKycVerify")] public int AdditionalKycVerify { get; set; }
        [JsonPropertyName("takerUserNo")] public string? TakerUserNo { get; set; }
    }

    public class UserOrderDetailApiResponse
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public OrderDetailData? Data { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; }
    }

    public class kycDetail
    {
        public DateTime? KycDate { get; set; }
        public int? KycLevel { get; set; }
        public bool? KycAvailable { get; set; }
        public float? Acumulado { get; set; }

        public DateTime? UltimaOrden { get; set; }

        public int? CountOrderes { get; set; }

        public string? Nationality { get; set; }
    }

    public class N8nWebhookResponse
    {
        [JsonPropertyName("nombre")] public string? Nombre { get; set; }
        [JsonPropertyName("identificacion")] public string? Identificacion { get; set; }
        [JsonPropertyName("tipoidentificacion")] public string? TipoIdentificacion { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; }
    }

    private static async Task OnWebSocketMessageReceived(ClientWebSocket ws, string messageJson)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Received WS Message: {messageJson}");
        Console.ResetColor();

        string WebSocketLogFilePath = string.Empty;
        try
        {
            var message = JsonSerializer.Deserialize<WebSocketMessage>(messageJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            try
            {
                WebSocketLogFilePath = $"messages-{(string.IsNullOrEmpty(message?.OrderNo) ? message?.TopicId : message.OrderNo)}.log";
                string log = $"---" +
                    $"CreateTime: {message?.CreateTime}" +
                    $"From: {(string.IsNullOrEmpty(message?.FromNickname) ? "" : message.FromNickname)}" +
                    $"Content: {message?.Content}" +
                    $"Type: {message?.Type}";
                // Añade una nueva línea después de cada mensaje para mejor legibilidad en el archivo
                await File.AppendAllTextAsync(WebSocketLogFilePath, log + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing to log file {WebSocketLogFilePath}: {ex.Message}");
            }
            //if (message != null && message.TopicId == "22757815628329472000" && !message.Self)
            if (message != null && message.Type == "system" && !string.IsNullOrEmpty(message.Content) && message.Content.Contains("order_created_with_additional_kyc_maker_buy"))
            ////if (true)
            {
                // // --- Consultar API de Binance para conocer los datos de la orden ---
                string orderDetailPath = "/sapi/v1/c2c/orderMatch/getUserOrderDetail";
                var orderDetailBody = new Dictionary<string, string> {
                    { "adOrderNo", string.IsNullOrEmpty(message?.OrderNo) ? message?.TopicId : message.OrderNo}
                    //{"adOrderNo", "22758202657778237440" }
                    };

                var orderDetailResponse = await SendSignedRequestAsync<UserOrderDetailApiResponse>(
                    HttpMethod.Post,
                    $"{BASE_URL}{orderDetailPath}",
                    payload: null,
                    // O new Dictionary<string, object>()
                    dataLoad: orderDetailBody);

                if (orderDetailResponse?.Data != null && orderDetailResponse.Success)
                {
                    string nDocumento = ExtractNumericCharacters(orderDetailResponse.Data.PayMethods?.First()?.Fields?.First(p => p.FieldName == "ID Number").FieldValue);
                    var culturaCo = new CultureInfo("es-CO");
                    string chatReplyContent = $"* Nombre Completo: {orderDetailResponse.Data.PayMethods?.First()?.Fields?.First(p => p.FieldName == "Name").FieldValue}\r\n" +
                      $"* Nro. Documento: {nDocumento} \r\n" +
                      $"* Tipo Documento: {IdentifyColombianDocumentType(nDocumento)} \r\n" +
                      $"* Nro. Cuenta: {ExtractNumericCharacters(orderDetailResponse.Data.PayMethods?.First()?.Fields?.First(p => p.FieldName == "Account number").FieldValue)} \r\n" +
                      $"* Tipo Cuenta: {orderDetailResponse.Data.PayMethods?.First()?.Fields?.First(p => p.FieldName == "Account type").FieldValue}\r\n" +
                      $"* Monto: {GetIntegerPart(orderDetailResponse.Data.TotalPrice)}\r\n";
                    var replyPayload = new ChatMessagePayload
                    {
                        OrderNo = string.IsNullOrEmpty(message?.OrderNo) ? message?.TopicId : message.OrderNo, // El ID de la conversación/orden del chat
                        Content = chatReplyContent, // Mensaje con detalles de la orden
                        Uuid = Guid.NewGuid().ToString() // Nuevo UUID para este mensaje
                    };

                    string replyJson = JsonSerializer.Serialize(replyPayload);
                    byte[] replyBytes = Encoding.UTF8.GetBytes(replyJson); var segment = new
                    ArraySegment<byte>(replyBytes);
                    await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);

                    var _result = NeedKYCAsync(PostgresDb, orderDetailResponse.Data.TakerUserNo ?? string.Empty).Result;
                    //var _result = NeedKYCAsync(PostgresDb, "s3c147a78d90c3c2eb9b234db1c34d1a7").Result;
                    kycDetail _kycDetails = _result.Item2;

                    if (_result.Item1)
                    {
                        int lvlrequired = _kycDetails.Acumulado <= Level1 ? 1 : (_kycDetails.Acumulado <= Level2 ? 2 : 3);

                        if (lvlrequired <= _kycDetails.KycLevel)
                        {
                            chatReplyContent = $"✅Hola, cómo estás? Confirmame si los datos del perfil son correctos por favor.";
                        }
                        else
                        {
                            chatReplyContent = $"Hola, ¿cómo estás? Con esta operación pasamos los {(lvlrequired == 2 ? "10.000" : "100.000")} usd en transacciones durante los últimos 30 días, por esta razón requerimos verificar el origen de fondos, por favor enviarnos declaración de renta y 3 últimos extractos Bancarios.";
                        }

                        Thread.Sleep(500);

                        replyPayload = new ChatMessagePayload
                        {
                            OrderNo = string.IsNullOrEmpty(message?.OrderNo) ? message?.TopicId : message.OrderNo, // El ID de la conversación/orden del chat
                            Content = chatReplyContent, // Mensaje con detalles de la orden
                            Uuid = Guid.NewGuid().ToString() // Nuevo UUID para este mensaje
                        };

                        replyJson = JsonSerializer.Serialize(replyPayload);
                        replyBytes = Encoding.UTF8.GetBytes(replyJson);
                        segment = new ArraySegment<byte>(replyBytes);
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    if (!_result.Item1)
                    {
                        chatReplyContent = $"{FormGoogle.Replace("[ordenID]", string.IsNullOrEmpty(message?.OrderNo) ? message?.TopicId : message.OrderNo).Replace("[Monto]", GetIntegerPart(orderDetailResponse.Data.TotalPrice))}";
                        Thread.Sleep(500);

                        replyPayload = new ChatMessagePayload
                        {
                            OrderNo = string.IsNullOrEmpty(message?.OrderNo) ? message?.TopicId : message.OrderNo, // El ID de la conversación/orden del chat
                            Content = chatReplyContent, // Mensaje con detalles de la orden
                            Uuid = Guid.NewGuid().ToString() // Nuevo UUID para este mensaje
                        };

                        replyJson = JsonSerializer.Serialize(replyPayload);
                        replyBytes = Encoding.UTF8.GetBytes(replyJson);
                        segment = new ArraySegment<byte>(replyBytes);
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);

                        //tercer mensaje
                        Thread.Sleep(3000);
                        int monto = Convert.ToInt32(GetIntegerPart(orderDetailResponse.Data.TotalPrice));
                        if (monto < Level1)
                        {
                            chatReplyContent = $"🚨🚨🚨" +
                                $"Hola, cómo estás? para proceder requerimos verificar tú identidad, por favor llenar el formulario.";
                        }
                        else
                        {
                            chatReplyContent = $"🚨🚨🚨 Hola, como estás? Con esta operación superamos los 10.000 usd en transacciones, " +
                                $"por esta razón requerimos documentos soporte del origen de fondos, " +
                                $"Porfavor llena el formulario que te enviamos y a nuestro whatsapp +573025607764 envíanos tu declaración de renta " +
                                $"y últimos 3 extractos bancarios por favor, quedamos atentos para poder proceder con el pago";
                        }
                        replyPayload = new ChatMessagePayload
                        {
                            OrderNo = string.IsNullOrEmpty(message?.OrderNo) ? message?.TopicId : message.OrderNo, // El ID de la conversación/orden del chat
                            Content = chatReplyContent, // Mensaje con detalles d  e la orden
                            Uuid = Guid.NewGuid().ToString() // Nuevo UUID para este mensaje
                        };

                        replyJson = JsonSerializer.Serialize(replyPayload);
                        replyBytes = Encoding.UTF8.GetBytes(replyJson);
                        segment = new ArraySegment<byte>(replyBytes);
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }

                    Console.WriteLine($"Sent reply based on order details: {replyJson}");
                }
                else
                {
                    Console.WriteLine($"Failed to get order details. Code: {orderDetailResponse?.Code}, Message: {orderDetailResponse?.Message}");
                }
            }

            //proceso para n8n
            if (message != null &&
               message.Type == "text" && !string.IsNullOrEmpty(message.OrderNo) && !string.IsNullOrEmpty(message.FromNickname))
            {
                //validacion para saber si enviamos mensaje al n8n o no.
                string uriPath = $"{BASE_URL}/sapi/v1/c2c/chat/retrieveChatMessagesWithPagination";
                var param = new Dictionary<string, object>()
                {
                    { "orderNo", message.OrderNo },
                    { "page", 1 },
                    { "rows", 300 }
                };
                //var allChatMessagesResponse = await SendSignedRequestAsync<ApiAllChatMessagesWithPaginationResponse>(HttpMethod.Get, uriPath, param);
                //Console.WriteLine($"mensaje de n8n: {allChatMessagesResponse.Data}");
                //if (allChatMessagesResponse.Code == null
                //    || allChatMessagesResponse.Data == null
                //    || allChatMessagesResponse.Total <= 4)
                //{
                //    //Console.WriteLine($"Failed to retrieve chat credentials. Code: {chatCredResponse?.Code}, Message: {chatCredResponse?.Message}");
                //    //Console.WriteLine("Presiona cualquier tecla para salir.");
                //    //Console.ReadKey();
                //    return;
                //}

                //validacion para saber si enviamos mensaje al n8n o no.
                //uriPath = N8NURL;
                // param = new Dictionary<string, object>();
                //message.isBuyer = message.Self; // Asignar el valor de Self a isBuyer
                //var n8nResponse = await SendSignedRequestAsync<N8nWebhookResponse>(HttpMethod.Post, uriPath, param, dataLoad: message);
                //Console.WriteLine($"n8n Nombre: {n8nResponse?.Nombre}, /n Identificacion: {n8nResponse?.Identificacion}" + $"/n TipoIdentificacion: {n8nResponse?.TipoIdentificacion}");
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Error deserializing WebSocket message: {ex.Message}. JSON: {messageJson}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing WebSocket message: {ex.ToString()}");
        }
    }

    public static async Task<(bool, kycDetail)> NeedKYCAsync(string? connectionString, string TakerUserNo)
    {
        string mesa = string.Empty;
        kycDetail _kycDetails = new kycDetail();
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            DateTime CreateTime = new DateTime(2025, 08, 09);
            if (DateTime.Now.AddDays(-30) > CreateTime)
            {
                CreateTime = DateTime.Now.AddDays(-30);
            }

            await using (var cmdCheck = new NpgsqlCommand(@"select  u.""KycAvailable"",u.""KycDate"",u.""KycLevel"",COUNT(0) CountOrderes,
                                                                    SUM(od.""TotalPrice"" ) Acumulado, max(od.""CreateTime""), U.""Nationality""
                                                    from ""Users"" u
                                                    left join ""OrderDetails"" od on u.""TakerUserNo"" = od.""TakerUserNo"" and od.""CreateTime"" > @CreateTime
                                                    where u.""TakerUserNo"" = @TakerUserNo
                                                    group by u.""KycAvailable"",u.""KycDate"",u.""KycLevel"" , U.""Nationality""; ", conn))
            {
                cmdCheck.Parameters.AddWithValue("TakerUserNo", TakerUserNo);
                cmdCheck.Parameters.AddWithValue("CreateTime", CreateTime);
                await using var dbReader = await cmdCheck.ExecuteReaderAsync();
                if (await dbReader.ReadAsync())
                {
                    if (!dbReader.IsDBNull(0))
                    {
                        _kycDetails.KycAvailable = dbReader.GetBoolean(0);
                    }

                    if (!dbReader.IsDBNull(1))
                    {
                        _kycDetails.KycDate = dbReader.GetDateTime(1);
                    }
                    if (!dbReader.IsDBNull(2))
                    {
                        _kycDetails.KycLevel = dbReader.GetInt32(2);
                    }
                    if (!dbReader.IsDBNull(3))
                    {
                        _kycDetails.CountOrderes = dbReader.GetInt32(3);
                    }
                    else
                    { _kycDetails.CountOrderes = 0; }

                    if (!dbReader.IsDBNull(4))
                    {
                        _kycDetails.Acumulado = dbReader.GetFloat(4);
                    }
                    if (!dbReader.IsDBNull(5))
                    {
                        _kycDetails.UltimaOrden = dbReader.GetDateTime(5);
                    }
                    if (!dbReader.IsDBNull(6))
                    {
                        _kycDetails.Nationality = dbReader.GetString(6);
                    }
                }
            }
            DateTime KYCCreateTime = new DateTime(2025, 08, 09);
            if (_kycDetails.KycDate > KYCCreateTime)
            {
                KYCCreateTime = DateTime.Now.AddDays(-90);
            }
            if (string.IsNullOrEmpty(_kycDetails.Nationality))
            {
                return (false, _kycDetails);
            }
            if (_kycDetails.CountOrderes > 0)
            {
                //si la fehca de kyc es mayor a los 90 dias anteriroes es true  de lo contrario false, lo cual va hacer que se realice de nuevo el KYC
                return (_kycDetails.KycDate >= KYCCreateTime, _kycDetails);
            }

            //bool t = (bool)(kycAvailable == null ? false : kycAvailable);
            //return ((bool)(kycAvailable == null ? false : kycAvailable), (int)(kycLevel == null ? 0 : kycLevel));
        }
        catch (Exception ex)
        {
            mesa = ex.Message;
            //throw ex;
        }
        return (false, _kycDetails);
    }

    public static async Task Main(string[] args)
    {
        while (true)
        {
            Console.WriteLine("Attempting to retrieve chat credentials...");
            string uriPath = $"{BASE_URL}/sapi/v1/c2c/chat/retrieveChatCredential";
            var param = new Dictionary<string, object>();

            var chatCredResponse = await SendSignedRequestAsync<ApiChatCredentialResponse>(HttpMethod.Get, uriPath, param);

            if (chatCredResponse?.Data?.ChatWssUrl == null || chatCredResponse.Data.ListenKey == null || chatCredResponse.Data.ListenToken == null)
            {
                //Console.WriteLine($"Failed to retrieve chat credentials. Code: {chatCredResponse?.Code}, Message: {chatCredResponse?.Message}");
                Console.WriteLine("Presiona cualquier tecla para salir.");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("Credentials retrieved successfully.");

            string wssUrl = $"{chatCredResponse.Data.ChatWssUrl}/{chatCredResponse.Data.ListenKey}?token={chatCredResponse.Data.ListenToken}&clientType=web";
            Console.WriteLine($"Connecting to WebSocket: {wssUrl}");

            using (var ws = new ClientWebSocket())
            {
                try
                {
                    await ws.ConnectAsync(new Uri(wssUrl), CancellationToken.None);
                    Console.WriteLine("WebSocket connected!");

                    var buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            await OnWebSocketMessageReceived(ws, receivedMessage);
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Console.WriteLine($"WebSocket closed by server. Status: {result.CloseStatus}, Description: {result.CloseStatusDescription}");
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                            break;
                        }
                    }
                }
                catch (WebSocketException ex)
                {
                    Console.WriteLine($"WebSocket error: {ex.Message} (Code: {ex.WebSocketErrorCode}, NativeErr: {ex.NativeErrorCode})");
                    if (ex.InnerException != null) Console.WriteLine($"Inner WebSocket error: {ex.InnerException.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"An unexpected error occurred: {ex.ToString()}");
                }
                finally
                {
                    if (ws.State != WebSocketState.Closed && ws.State != WebSocketState.Aborted)
                    {
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                        catch (Exception e) { Console.WriteLine($"Error during final WS close: {e.Message}"); }
                    }
                    Console.WriteLine("WebSocket connection attempt finished.");
                }
            }
        }
        Console.WriteLine("Program finished. Press any key to exit.");
        Console.ReadKey();
    }

    /// <summary>
    /// Extrae caracteres numéricos de un string. Si el string comienza con dos letras y el TERCER
    /// carácter es un DÍGITO, esas dos letras se conservan junto con todos los números
    /// subsiguientes. En otros casos, solo se extraen los números de todo el string. Elimina otros
    /// caracteres especiales y espacios.
    /// </summary>
    /// <param name="inputString">El string de entrada.</param>
    /// <returns>Un nuevo string procesado según las reglas.</returns>
    public static string ExtractNumericCharacters(string? inputString)
    {
        if (string.IsNullOrEmpty(inputString))
        {
            return string.Empty;
        }

        StringBuilder resultBuilder = new StringBuilder();

        // Verificar si el string comienza con dos letras Y el tercer carácter es un dígito
        if (inputString.Length >= 3 &&
            char.IsLetter(inputString[0]) &&
            char.IsLetter(inputString[1]) &&
            char.IsDigit(inputString[2]))
        {
            resultBuilder.Append(inputString[0]); // Conservar la primera letra
            resultBuilder.Append(inputString[1]); // Conservar la segunda letra
            // El tercer carácter (dígito) ya está validado, así que se incluirá en el bucle de
            // abajo si empezamos desde i=2. O podemos añadirlo explícitamente y empezar el bucle
            // desde i=3. Lo añadiré en el bucle.

            // Añadir todos los dígitos del resto del string (a partir del tercer carácter)
            for (int i = 2; i < inputString.Length; i++)
            {
                if (char.IsDigit(inputString[i]))
                {
                    resultBuilder.Append(inputString[i]);
                }
            }
        }
        else // Si no cumple la condición (LLD...) extraer solo los dígitos de todo el string
        {
            foreach (char c in inputString)
            {
                if (char.IsDigit(c))
                {
                    resultBuilder.Append(c);
                }
            }
        }

        return resultBuilder.ToString();
    }

    public static string GetIntegerPart(string? numericString)
    {
        if (string.IsNullOrWhiteSpace(numericString))
        {
            return string.Empty;
        }

        int decimalPointIndex = numericString.IndexOf('.');

        if (decimalPointIndex == -1)
        {
            // No se encontró punto decimal, se asume que todo el string es la parte entera. Se
            // podría añadir una validación para asegurar que es realmente numérico si es necesario.
            return numericString;
        }
        else if (decimalPointIndex == 0)
        {
            // El string empieza con un punto decimal, ej. ".98"
            return "0";
        }
        else
        {
            // Se encontró un punto decimal, se toma la subcadena anterior.
            return numericString.Substring(0, decimalPointIndex);
        }
    }

    /// <summary>
    /// Identifica el tipo de documento colombiano basado en el formato del string proporcionado.
    /// Tipos reconocidos: "CC" (Cédula de Ciudadanía), "Pasaporte", "CE" (Cédula de Extranjería),
    /// "PPT" (Permiso por Protección Temporal), "NIT" (Número de Identificación Tributaria).
    /// Devuelve "N/A" si no coincide con ningún formato conocido.
    ///
    /// Suposiciones de formato:
    /// - Pasaporte: 2 letras seguidas de 7 dígitos (ej. AA1234567).
    /// - NIT: 9 dígitos puramente numéricos comenzando con '8' o '9'. (La validación de NIT con
    /// guion ha sido eliminada).
    /// - PPT: Exactamente 7 dígitos numéricos (después de limpieza).
    /// - CC: 8, 9 (si no empieza con '8' o '9') o 10 dígitos numéricos (después de limpieza).
    /// - CE: Entre 3 y 6 dígitos numéricos (después de limpieza). La función prioriza formatos
    /// específicos (Pasaporte) y luego NIT de 9 dígitos (8xx, 9xx).
    /// </summary>
    /// <param name="rawDocNumberString">El string del número de documento a identificar.</param>
    /// <returns>Un string indicando el tipo de documento o "N/A".</returns>
    public static string IdentifyColombianDocumentType(string? rawDocNumberString)
    {
        if (string.IsNullOrWhiteSpace(rawDocNumberString))
        {
            return "N/A";
        }

        string trimmedInput = rawDocNumberString.Trim();

        // 1. Verificar Pasaporte (formato LLNNNNNNN en el input original)
        if (Regex.IsMatch(trimmedInput, @"^[A-Za-z]{2}\d{7}$"))
        {
            return "Pasaporte";
        }

        // La validación para NIT con formato XXXXXXXXX-Y ha sido eliminada según solicitud. if
        // (Regex.IsMatch(trimmedInput, @"^\d{9}-\d{1}$")) { return "NIT"; }

        string processedDocString = ExtractNumericCharacters(trimmedInput);

        // 2. Re-verificar Pasaporte en el string procesado (Ej: si el original fue "AA123XYZ4567",
        // processedDocString será "AA1234567")
        if (processedDocString.Length == 9 &&
            char.IsLetter(processedDocString[0]) &&
            char.IsLetter(processedDocString[1]) &&
            processedDocString.Substring(2).All(char.IsDigit))
        {
            return "Pasaporte";
        }

        string purelyNumericPart;
        if (processedDocString.Any(char.IsLetter))
        {
            // Si processedDocString contiene letras (ej. "AA123" de ExtractNumericCharacters),
            // extraer solo la parte numérica para las siguientes validaciones.
            StringBuilder tempNumeric = new StringBuilder();
            foreach (char c in processedDocString)
            {
                if (char.IsDigit(c))
                {
                    tempNumeric.Append(c);
                }
            }
            purelyNumericPart = tempNumeric.ToString();
        }
        else
        {
            // processedDocString ya era puramente numérico.
            purelyNumericPart = processedDocString;
        }

        // En este punto, purelyNumericPart debería contener solo dígitos o ser vacío. Si está
        // vacío, no coincide con los tipos de documentos numéricos.
        if (string.IsNullOrEmpty(purelyNumericPart))
        {
            return "N/A";
        }

        int len = purelyNumericPart.Length;

        //1 pasaporte
        if (len == 10 && (purelyNumericPart.StartsWith("50") || purelyNumericPart.StartsWith("51") || purelyNumericPart.StartsWith("60")))
        {
            return "Pasaporte";
        }
        // 2. Verificar NIT (9 dígitos puramente numéricos comenzando con '8' o '9')
        if ((len == 9 || len == 10) && (purelyNumericPart.StartsWith("8") || purelyNumericPart.StartsWith("9")))
        {
            return "NIT";
        }

        // 3. Verificar PPT (Permiso por Protección Temporal)
        if (len == 7)
        {
            return "PPT";
        }

        // 4. Verificar CE (Cédula de Extranjería)
        if (len >= 3 && len <= 6)
        {
            return "CE";
        }

        // 5. Verificar CC (Cédula de Ciudadanía) - 8, 9 (si no es NIT) o 10 dígitos
        if (len == 8 || len == 10 || (len == 9 && !(purelyNumericPart.StartsWith("8") || purelyNumericPart.StartsWith("9"))))
        {
            return "CC";
        }

        // 6. Si no coincide con ninguno de los formatos anteriores
        return "N/A";
    }
}