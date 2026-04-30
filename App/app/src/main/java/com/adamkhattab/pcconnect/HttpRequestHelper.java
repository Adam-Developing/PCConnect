package com.adamkhattab.pcconnect;

import android.content.Context;
import android.os.AsyncTask;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;

public class HttpRequestHelper {

    private static final HttpRequestHelper instance = new HttpRequestHelper();

    private HttpRequestHelper() {
    }

    public static HttpRequestHelper getInstance() {
        return instance;
    }

    public interface ApiResponseCallback {
        void onResponse(String response);
    }

    public void makeApiRequest(String apiUrl, String apiKey, ApiResponseCallback callback) {
        new ApiRequestTask(apiUrl, apiKey, callback).execute();
    }

    private static class ApiRequestTask extends AsyncTask<Void, Void, String> {
        private final String apiUrl;
        private final String apiKey;
        private final ApiResponseCallback callback;

        public ApiRequestTask(String apiUrl, String apiKey, ApiResponseCallback callback) {
            this.apiUrl = apiUrl;
            this.apiKey = apiKey;
            this.callback = callback;
        }

        @Override
        protected String doInBackground(Void... params) {
            try {
                URL url = new URL(apiUrl);
                HttpURLConnection connection = (HttpURLConnection) url.openConnection();
                connection.setRequestProperty("X-API-Key", apiKey);

                BufferedReader reader = new BufferedReader(new InputStreamReader(connection.getInputStream()));
                StringBuilder response = new StringBuilder();
                String line;
                while ((line = reader.readLine()) != null) {
                    response.append(line);
                }
                reader.close();

                return response.toString();
            } catch (Exception e) {
                e.printStackTrace();
            }
            return null;
        }

        @Override
        protected void onPostExecute(String response) {
            if (callback != null) {
                callback.onResponse(response);
            }
        }
    }
}
