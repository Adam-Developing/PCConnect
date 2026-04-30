package com.adamkhattab.pcconnect;

import android.content.Intent; // Add this import statement
import android.os.Bundle;
import android.view.View;
import android.widget.Button;

import androidx.appcompat.app.AppCompatActivity;

public class ReminderSuccess extends AppCompatActivity {

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        setContentView(R.layout.reminder_success);

        Button DoneBTN = findViewById(R.id.DoneBTN);
        DoneBTN.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                Intent intent = new Intent(ReminderSuccess.this, MainActivity.class);
                startActivity(intent);
            }
        });
    }


}
