package dev.notaekinesis.core.api

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.Job
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.KSerializer
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** The session is gone (expired or revoked); the user has to sign in again. */
class UnauthorizedException : IOException("Signed out")

/** The server refused a request; [message] is fit to show. */
class ApiException(message: String, val status: Int) : IOException(message)

/** Talks to the notaekinesis server with a bearer session token. */
class ApiClient(
    serverUrl: String,
    token: String?,
    base: OkHttpClient = OkHttpClient(),
    userAgent: String = "notaekinesis-android/0.1",
) {
    val baseUrl: HttpUrl = (serverUrl.trimEnd('/') + "/").toHttpUrl()

    val http: OkHttpClient = base.newBuilder()
        .callTimeout(30, TimeUnit.SECONDS)
        .apply { interceptors().add(0, auth(token, userAgent)) } // ahead of the caller's own (a test's fake server)
        .build()

    /** Adds the session to requests for the server only, never to other hosts (redirects, previews). */
    private fun auth(token: String?, userAgent: String) = Interceptor { chain ->
        val req = chain.request()
        val builder = req.newBuilder().header("User-Agent", userAgent)
        if (token != null && req.url.host == baseUrl.host && req.url.port == baseUrl.port)
            builder.header("Authorization", "Bearer $token")
        chain.proceed(builder.build())
    }

    /** The server sends a heartbeat every 20 s, so a silent minute means the connection is gone. */
    private val events = http.newBuilder().callTimeout(0, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build()

    fun url(path: String): HttpUrl = baseUrl.resolve(path)!!

    private suspend fun <T> send(request: Request, type: KSerializer<T>): T {
        val res = http.newCall(request).await()
        res.use {
            check(it)
            return ApiJson.decodeFromString(type, it.body.string())
        }
    }

    private fun check(res: Response) {
        if (res.code == 401) throw UnauthorizedException()
        if (!res.isSuccessful) throw ApiException(errorMessage(res.body.string().trim(), res.code), res.code)
    }

    suspend fun me(): User = send(Request.Builder().url(url("api/me")).build(), User.serializer())

    /** Trades the one-time token from the native login page for a session. */
    suspend fun verifyOneTimeToken(token: String): VerifyResponse =
        send(post("api/auth/one-time-token/verify", ApiJson.encodeToString(VerifyBody.serializer(), VerifyBody(token))), VerifyResponse.serializer())

    suspend fun signOut() {
        http.newCall(post("api/auth/sign-out", "{}")).await().close()
    }

    suspend fun push(mutations: List<Mutation>): PushResponse =
        send(post("api/sync/push", ApiJson.encodeToString(PushBody.serializer(), PushBody(mutations))), PushResponse.serializer())

    suspend fun pull(since: Long, limit: Int = 500): PullResponse = send(
        Request.Builder().url(url("api/sync/pull").newBuilder()
            .addQueryParameter("since", since.toString())
            .addQueryParameter("limit", limit.toString()).build()).build(),
        PullResponse.serializer(),
    )

    /** Uploads a file the way a browser form would (a quoted UTF-8 file name and a typed part), which is what the server expects. */
    suspend fun upload(content: RequestBody, fileName: String): FileDto {
        val form = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("file", fileName.filter { it != '\r' && it != '\n' }, content)
            .build()
        val req = Request.Builder().url(url("api/files")).post(form).build()
        // Big files on a slow connection take longer than the usual call timeout.
        val res = http.newBuilder().callTimeout(0, TimeUnit.SECONDS).writeTimeout(60, TimeUnit.SECONDS).build().newCall(req).await()
        res.use {
            check(it)
            return ApiJson.decodeFromString(FileDto.serializer(), it.body.string())
        }
    }

    fun fileUrl(fileId: String): HttpUrl = url("api/files/").newBuilder().addPathSegment(fileId).build()

    /** The server's revision, every time it changes (Server-Sent Events). Completes when the connection drops. */
    fun events(): Flow<Long> = flow {
        val call = events.newCall(Request.Builder().url(url("api/sync/events")).header("Accept", "text/event-stream").build())
        currentCoroutineContext()[Job]?.invokeOnCompletion { call.cancel() }
        call.execute().use { res ->
            check(res)
            val source = res.body.source()
            var data: String? = null
            while (true) {
                val line = try {
                    source.readUtf8Line()
                } catch (_: IOException) {
                    null // no heartbeat, or cancelled
                } ?: break
                when {
                    line.startsWith("data:") -> data = line.substring(5).trim()
                    line.isEmpty() && data != null -> {
                        val rev = try {
                            ApiJson.decodeFromString(RevEvent.serializer(), data).rev
                        } catch (_: SerializationException) {
                            null
                        } catch (_: IllegalArgumentException) {
                            null
                        }
                        data = null
                        if (rev != null) emit(rev)
                    }
                }
            }
        }
    }.flowOn(Dispatchers.IO)

    private fun post(path: String, json: String) =
        Request.Builder().url(url(path)).post(json.toRequestBody(JSON)).build()

    companion object {
        private val JSON = "application/json; charset=utf-8".toMediaType()

        /**
         * An error message fit to show: the server answers with plain text ("Files can be at most
         * 100 MB") or, for a validation failure, JSON whose "summary" says what was wrong.
         */
        fun errorMessage(body: String, status: Int): String {
            var text = body
            if (text.startsWith("{")) {
                try {
                    val json = ApiJson.parseToJsonElement(text).jsonObject
                    for (key in listOf("summary", "message", "error")) {
                        val v = json[key]
                        if (v is JsonPrimitive && v.isString && v.content.isNotEmpty()) return v.content
                    }
                } catch (_: SerializationException) {
                } catch (_: IllegalArgumentException) {
                }
                text = ""
            }
            if (text.isEmpty() || text.startsWith("<")) return "The server answered $status."
            return if (text.length > 200) text.take(200) + "…" else text
        }
    }
}

/** Runs the call without blocking a thread; cancelling the coroutine cancels the call. */
suspend fun Call.await(): Response = suspendCancellableCoroutine { cont ->
    cont.invokeOnCancellation { cancel() }
    enqueue(object : Callback {
        override fun onResponse(call: Call, response: Response) = cont.resume(response) { _, r, _ -> r.close() }
        override fun onFailure(call: Call, e: IOException) = cont.resumeWithException(e)
    })
}
