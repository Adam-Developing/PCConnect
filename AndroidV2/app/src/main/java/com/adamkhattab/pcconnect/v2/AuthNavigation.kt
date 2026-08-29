package com.adamkhattab.pcconnect.v2

import android.os.Build
import android.util.Patterns
import androidx.activity.compose.BackHandler
import androidx.activity.compose.LocalActivity
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.adamkhattab.pcconnect.v2.data.PlatformCapabilities

private object AuthRoute {
    const val SIGN_IN = "auth/sign-in"
    const val REGISTER = "auth/register"
    const val FORGOT_PASSWORD = "auth/forgot-password"
    const val RESET_PASSWORD = "auth/reset-password"
}

@Composable
internal fun AuthNavigation(viewModel: MainViewModel) {
    val resetToken by viewModel.passwordResetToken.collectAsStateWithLifecycle()
    val navController = rememberNavController()

    LaunchedEffect(resetToken) {
        when {
            resetToken != null && navController.currentDestination?.route != AuthRoute.RESET_PASSWORD -> {
                navController.navigate(AuthRoute.RESET_PASSWORD) {
                    popUpTo(AuthRoute.SIGN_IN)
                    launchSingleTop = true
                }
            }

            resetToken == null && navController.currentDestination?.route == AuthRoute.RESET_PASSWORD -> {
                navController.returnToSignIn()
            }
        }
    }

    NavHost(
        navController = navController,
        startDestination = if (resetToken == null) AuthRoute.SIGN_IN else AuthRoute.RESET_PASSWORD,
        modifier = Modifier.fillMaxSize().navigationBarsPadding(),
    ) {
        composable(AuthRoute.SIGN_IN) {
            SignInScreen(
                viewModel = viewModel,
                onCreateAccount = {
                    viewModel.clearSignInPassword()
                    navController.navigate(AuthRoute.REGISTER) { launchSingleTop = true }
                },
                onForgotPassword = {
                    viewModel.clearSignInPassword()
                    navController.navigate(AuthRoute.FORGOT_PASSWORD) { launchSingleTop = true }
                },
            )
        }
        composable(AuthRoute.REGISTER) {
            val onBack = {
                viewModel.clearRegistrationPassword()
                navController.popBackStack()
                Unit
            }
            RegisterScreen(viewModel = viewModel, onBack = onBack)
        }
        composable(AuthRoute.FORGOT_PASSWORD) {
            val onBack = {
                navController.popBackStack()
                Unit
            }
            ForgotPasswordScreen(viewModel = viewModel, onBack = onBack)
        }
        composable(AuthRoute.RESET_PASSWORD) {
            PasswordResetScreen(viewModel = viewModel, onCancel = viewModel::cancelPasswordReset)
        }
    }
}

private fun NavHostController.returnToSignIn() {
    navigate(AuthRoute.SIGN_IN) {
        popUpTo(AuthRoute.RESET_PASSWORD) { inclusive = true }
        launchSingleTop = true
    }
}

@Composable
private fun SignInScreen(
    viewModel: MainViewModel,
    onCreateAccount: () -> Unit,
    onForgotPassword: () -> Unit,
) {
    val sensitive by viewModel.sensitiveUi.collectAsStateWithLifecycle()
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    var login by rememberSaveable { mutableStateOf("") }
    val password = sensitive.signInPassword
    val activity = LocalActivity.current
    val focusManager = LocalFocusManager.current

    Column(
        Modifier.fillMaxSize().padding(28.dp).verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("PCConnect", style = MaterialTheme.typography.headlineLarge)
        Text("Securely control your enrolled computers.")
        OutlinedTextField(
            login,
            { login = it.take(254) },
            label = { Text("Email or username") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            password,
            viewModel::updateSignInPassword,
            label = { Text("Password") },
            visualTransformation = PasswordVisualTransformation(),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = {
                focusManager.clearFocus()
                if (!busy && login.isNotBlank() && password.isNotBlank()) {
                    viewModel.login(login, password)
                }
            }),
            modifier = Modifier.fillMaxWidth(),
        )
        Button(
            { viewModel.login(login, password) },
            enabled = !busy && login.isNotBlank() && password.isNotBlank(),
            modifier = Modifier.fillMaxWidth(),
        ) { Text("Sign in") }
        if (PlatformCapabilities.supportsPasskeys(Build.VERSION.SDK_INT)) {
            OutlinedButton(
                { viewModel.loginWithPasskey(checkNotNull(activity), login) },
                enabled = !busy && activity != null,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("Sign in with a passkey") }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            TextButton(onCreateAccount) { Text("Create account") }
            TextButton(onForgotPassword) { Text("Forgot password?") }
        }
    }
}

@Composable
private fun RegisterScreen(viewModel: MainViewModel, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val sensitive by viewModel.sensitiveUi.collectAsStateWithLifecycle()
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    var email by rememberSaveable { mutableStateOf("") }
    var username by rememberSaveable { mutableStateOf("") }
    var displayName by rememberSaveable { mutableStateOf("") }
    var marketingOptIn by rememberSaveable { mutableStateOf(false) }
    val password = sensitive.registrationPassword
    val focusManager = LocalFocusManager.current
    val usernameLength = username.trim().length
    val usernameError = username.isNotEmpty() && usernameLength !in 3..50
    val emailValid = email.trim().let { it.length in 3..254 && Patterns.EMAIL_ADDRESS.matcher(it).matches() }
    val emailError = email.isNotEmpty() && !emailValid
    val displayNameLength = displayName.trim().length
    val displayNameError = displayName.isNotEmpty() && displayNameLength !in 1..100
    val passwordValid = password.length in 12..1024 && password.none(Char::isISOControl)
    val passwordError = password.isNotEmpty() && !passwordValid

    Column(
        Modifier.fillMaxSize().padding(28.dp).verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Create an account", style = MaterialTheme.typography.headlineMedium)
        OutlinedTextField(
            username,
            { username = it.take(50) },
            label = { Text("Username") },
            supportingText = if (usernameError) { { Text("Use 3–50 characters.") } } else null,
            isError = usernameError,
            singleLine = true,
            keyboardOptions = KeyboardOptions(
                capitalization = KeyboardCapitalization.None,
                keyboardType = KeyboardType.Text,
                imeAction = ImeAction.Next,
            ),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            email,
            { email = it.take(254) },
            label = { Text("Email") },
            supportingText = if (emailError) { { Text("Enter a valid email address.") } } else null,
            isError = emailError,
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            displayName,
            { displayName = it.take(100) },
            label = { Text("Display name") },
            supportingText = if (displayNameError) { { Text("Use 1–100 characters.") } } else null,
            isError = displayNameError,
            singleLine = true,
            keyboardOptions = KeyboardOptions(
                capitalization = KeyboardCapitalization.Words,
                imeAction = ImeAction.Next,
            ),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            password,
            viewModel::updateRegistrationPassword,
            label = { Text("Password") },
            supportingText = if (passwordError) { { Text("Use at least 12 characters.") } } else null,
            isError = passwordError,
            visualTransformation = PasswordVisualTransformation(),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
            modifier = Modifier.fillMaxWidth(),
        )
        Row(
            Modifier.fillMaxWidth().clickable { marketingOptIn = !marketingOptIn },
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Checkbox(marketingOptIn, { marketingOptIn = it })
            Text("Send me optional PCConnect product updates")
        }
        Button(
            { viewModel.register(username, email, displayName, password, marketingOptIn) },
            enabled = !busy && usernameLength in 3..50 && emailValid && displayNameLength in 1..100 && passwordValid,
            modifier = Modifier.fillMaxWidth(),
        ) { Text("Create account") }
        TextButton(onBack) { Text("Back to sign in") }
    }
}

@Composable
private fun ForgotPasswordScreen(viewModel: MainViewModel, onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    var email by rememberSaveable { mutableStateOf("") }
    val focusManager = LocalFocusManager.current
    val emailValid = email.trim().let { it.length in 3..254 && Patterns.EMAIL_ADDRESS.matcher(it).matches() }
    val emailError = email.isNotEmpty() && !emailValid

    Column(
        Modifier.fillMaxSize().padding(28.dp).verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Reset password", style = MaterialTheme.typography.headlineMedium)
        Text("Enter your email address. The response is the same whether or not an account exists.")
        OutlinedTextField(
            email,
            { email = it.take(254) },
            label = { Text("Email") },
            supportingText = if (emailError) { { Text("Enter a valid email address.") } } else null,
            isError = emailError,
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { focusManager.clearFocus() }),
            modifier = Modifier.fillMaxWidth(),
        )
        Button(
            { viewModel.requestPasswordReset(email) },
            enabled = !busy && emailValid,
            modifier = Modifier.fillMaxWidth(),
        ) { Text("Send reset link") }
        TextButton(onBack) { Text("Back to sign in") }
    }
}

@Composable
private fun PasswordResetScreen(viewModel: MainViewModel, onCancel: () -> Unit) {
    BackHandler(onBack = onCancel)
    val sensitive by viewModel.sensitiveUi.collectAsStateWithLifecycle()
    val password = sensitive.resetPassword
    val confirmation = sensitive.resetConfirmation
    val busy by viewModel.busy.collectAsStateWithLifecycle()

    Column(
        Modifier.fillMaxSize().padding(28.dp).verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Choose a new password", style = MaterialTheme.typography.headlineMedium)
        Text("This will revoke every existing PCConnect session.")
        OutlinedTextField(
            password,
            viewModel::updateResetPassword,
            label = { Text("New password") },
            supportingText = if (password.isNotEmpty() && password.length < 12) { { Text("Use at least 12 characters.") } } else null,
            isError = password.isNotEmpty() && password.length < 12,
            visualTransformation = PasswordVisualTransformation(),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            confirmation,
            viewModel::updateResetConfirmation,
            label = { Text("Confirm password") },
            supportingText = if (confirmation.isNotEmpty() && confirmation != password) { { Text("Passwords do not match.") } } else null,
            isError = confirmation.isNotEmpty() && confirmation != password,
            visualTransformation = PasswordVisualTransformation(),
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
            modifier = Modifier.fillMaxWidth(),
        )
        Button(
            { viewModel.completePasswordReset(password) },
            enabled = !busy && password.length >= 12 && password == confirmation,
            modifier = Modifier.fillMaxWidth(),
        ) { Text("Change password") }
        TextButton(onCancel) { Text("Cancel") }
    }
}
