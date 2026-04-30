package com.adamkhattab.pcconnect;

import java.io.IOException;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;
import okhttp3.FormBody;
import android.content.Context;


public class NetworkUtils {

    private static final MediaType JSON = MediaType.parse("application/json; charset=utf-8");

    public interface TimeCallback {
        void onTimeReceived(String time);
        void onError(String errorMessage);
    }



//    private static class FetchTimeTask extends AsyncTask<Void, Void, String> {
//        private static final String PHP_SCRIPT_URL = "http://your_server_domain/get_time.php";
//        private final TimeCallback callback;
//
//        FetchTimeTask(TimeCallback callback) {
//            this.callback = callback;
//        }
//
//        @Override
//        protected String doInBackground(Void... params) {
//            try {
//                URL url = new URL(PHP_SCRIPT_URL);
//                HttpURLConnection connection = (HttpURLConnection) url.openConnection();
//                connection.setRequestMethod("GET");
//
//                InputStream inputStream = connection.getInputStream();
//                BufferedReader bufferedReader = new BufferedReader(new InputStreamReader(inputStream));
//                StringBuilder stringBuilder = new StringBuilder();
//
//                String line;
//                while ((line = bufferedReader.readLine()) != null) {
//                    stringBuilder.append(line);
//                }
//
//                bufferedReader.close();
//                inputStream.close();
//                connection.disconnect();
//
//                return stringBuilder.toString();
//            } catch (IOException e) {
//                e.printStackTrace();
//                return null;
//            }
//        }
//
//        @Override
//        protected void onPostExecute(String result) {
//            if (result != null) {
//                try {
//                    JSONObject jsonObject = new JSONObject(result);
//                    String time = jsonObject.getString("time");
//                    callback.onTimeReceived(time);
//                } catch (JSONException e) {
//                    e.printStackTrace();
//                    callback.onError("Error parsing response");
//                }
//            } else {
//                callback.onError("Network request failed");
//            }
//        }
//    }


    public static void StateChange(Context context, String selectedValue, String Request) {
        OkHttpClient client = new OkHttpClient();

        String url = "https://pcconnect.adamkhattab.co.uk/api/pcconnect/exchange.php";

        // Create form data with id = 1
        RequestBody formBody = new FormBody.Builder()
                .add("Request", Request)
                .build();

        // Get the API key from shared preferences
        String apiKey = SharedPrefManager.getInstance(context).getApiKey();

        Request request = new Request.Builder()
                .url(url)
                .post(formBody)
                .addHeader("X-API-Key", apiKey)
                .addHeader("PCName", selectedValue)
                .build();

        client.newCall(request).enqueue(new Callback() {
            @Override
            public void onFailure(Call call, IOException e) {
                e.printStackTrace();
            }

            @Override
            public void onResponse(Call call, Response response) throws IOException {
                if (response.isSuccessful()) {
                    // Handle the successful response
                } else {
                    // Handle unsuccessful response
                }
                response.close();
            }
        });
    }




}
