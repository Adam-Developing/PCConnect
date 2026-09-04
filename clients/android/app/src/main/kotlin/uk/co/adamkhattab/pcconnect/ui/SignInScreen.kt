package uk.co.adamkhattab.pcconnect.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import uk.co.adamkhattab.pcconnect.R

@Composable
fun SignInScreen(
    state: AppState,
    onSignIn: (String, String) -> Unit,
    onRegister: (String, String, String) -> Unit,
    onForgotPassword: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    var login by rememberSaveable { mutableStateOf("") }
    var email by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    var registering by rememberSaveable { mutableStateOf(false) }
    var reveal by remember { mutableStateOf(false) }

    val canSubmit = login.isNotBlank() && password.isNotBlank() &&
        (!registering || email.isNotBlank()) && !state.isLoading

    Column(
        modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .imePadding()
            .padding(24.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Image(
            painter = painterResource(R.drawable.pcconnect_logo),
            contentDescription = null,
            modifier = Modifier.size(60.dp).clip(PcShapes.Tile),
            contentScale = ContentScale.Crop,
        )

        Box(Modifier.height(20.dp))

        Text(
            if (registering) "Create an account" else "Sign in",
            color = PcColors.Ink,
            style = PcType.Display,
        )

        Box(Modifier.height(8.dp))

        Text(
            if (registering) {
                "One account for every PC you sign in on, and for this phone."
            } else {
                "Change your PC's state from your phone, and set reminders that " +
                    "appear on the screen you're sitting at."
            },
            color = PcColors.InkSoft,
            style = PcType.BodySmall.copy(fontSize = 14.5.sp, lineHeight = 21.sp),
        )

        Box(Modifier.height(28.dp))

        PcTextField(
            value = login,
            onValueChange = { login = it },
            label = if (registering) "Username" else "Username or email",
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text),
        )

        if (registering) {
            Box(Modifier.height(14.dp))
            PcTextField(
                value = email,
                onValueChange = { email = it },
                label = "Email",
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
            )
        }

        Box(Modifier.height(14.dp))

        PcTextField(
            value = password,
            onValueChange = { password = it },
            label = "Password",
            // The mask hides it on screen; the password keyboard type is what
            // keeps it out of the suggestion strip and the learned-words
            // dictionary, where it would outlive the screen.
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            visualTransformation = if (reveal) VisualTransformation.None else PasswordVisualTransformation(),
            labelTrailing = if (registering) {
                null
            } else {
                {
                    TextLink("Forgot it?", onClick = { onForgotPassword(login.trim()) })
                }
            },
            trailing = {
                IconAction(
                    icon = if (reveal) PcIcons.VisibilityOff else PcIcons.Visibility,
                    contentDescription = if (reveal) "Hide password" else "Show password",
                    onClick = { reveal = !reveal },
                    tint = PcColors.InkFaint,
                )
            },
        )

        Box(Modifier.height(20.dp))

        PrimaryButton(
            text = if (registering) "Create account" else "Sign in",
            onClick = {
                if (registering) {
                    onRegister(login.trim(), email.trim(), password)
                } else {
                    onSignIn(login.trim(), password)
                }
            },
            enabled = canSubmit,
        )

        Box(
            Modifier.fillMaxWidth().height(46.dp),
            contentAlignment = Alignment.Center,
        ) {
            TextLink(
                text = if (registering) "I already have an account" else "Create an account",
                onClick = { registering = !registering },
                style = PcType.BodySmall.copy(fontSize = 14.5.sp),
            )
        }

        Box(Modifier.height(20.dp))

        InfoNote(
            text = "Install PCConnect on a Windows PC and sign in there with this " +
                "account. Add it to your phone from My PCs.",
            icon = PcIcons.Devices,
        )

        state.message?.let {
            Box(Modifier.height(16.dp))
            Text(it, color = PcColors.DangerInk, style = PcType.Caption)
        }
    }
}
