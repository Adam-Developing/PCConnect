SET FOREIGN_KEY_CHECKS = 0;
CREATE TABLE `users` (
  `id` int NOT NULL AUTO_INCREMENT,
  `Name` varchar(255) NOT NULL,
  `Username` varchar(255) NOT NULL,
  `DateOfBirth` varchar(255) NOT NULL,
  `Email` varchar(255) NOT NULL,
  `Password` varchar(64) NOT NULL,
  `Enabled` tinyint(1) NOT NULL DEFAULT '1',
  `DateTimeOfSignup` varchar(255) NOT NULL,
  `MailingList` tinyint(1) NOT NULL DEFAULT '0',
  `api_key` varchar(512) DEFAULT NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
CREATE TABLE `pcnames` (
  `PCID` int NOT NULL AUTO_INCREMENT,
  `UserID` int NOT NULL,
  `PCName` varchar(255) NOT NULL,
  `Request` varchar(512) DEFAULT '0',
  `Value` int DEFAULT 0,
  `Time` varchar(255) DEFAULT NULL,
  PRIMARY KEY (`PCID`),
  FOREIGN KEY (`UserID`) REFERENCES `users`(`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
CREATE TABLE `reminders` (
  `ID` int NOT NULL AUTO_INCREMENT,
  `UserID` int NOT NULL,
  `Time` time NOT NULL,
  `Reminder` text NOT NULL,
  `Completed` int NOT NULL DEFAULT '0',
  `Recurrence` varchar(255) DEFAULT 'none',
  `Recurrence_Frequency` varchar(255) DEFAULT NULL,
  `Recurrence_Day` varchar(255) DEFAULT NULL,
  `Recurrence_Time` time DEFAULT NULL,
  `Recurrence_End_Date` date DEFAULT NULL,
  `Date` date DEFAULT NULL,
  PRIMARY KEY (`ID`),
  FOREIGN KEY (`UserID`) REFERENCES `users`(`id`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;
-- phpMyAdmin SQL Dump
-- version 5.2.1
-- https://www.phpmyadmin.net/
--
-- Host: localhost
-- Generation Time: Apr 16, 2026 at 01:58 AM
-- Server version: 8.0.36
-- PHP Version: 8.3.15

SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO";
START TRANSACTION;
SET time_zone = "+00:00";


/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!40101 SET NAMES utf8mb4 */;

--
-- Database: `pcconnect`
--

-- --------------------------------------------------------

--
-- Table structure for table `apikeys`
--


--

-- --------------------------------------------------------

--
-- Table structure for table `code`
--

CREATE TABLE `code` (
  `ID` int NOT NULL,
  `Code` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

--


-- --------------------------------------------------------

--
-- Table structure for table `feedback`
--

CREATE TABLE `feedback` (
  `FeedbackID` int NOT NULL,
  `Name` text NOT NULL,
  `Email` text NOT NULL,
  `Feedback` text NOT NULL,
  `Rating` text NOT NULL,
  `IP` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

--


-- --------------------------------------------------------

--
-- Table structure for table `links`
--

CREATE TABLE `links` (
  `ID` int NOT NULL,
  `Name` text NOT NULL,
  `URL` text NOT NULL,
  `sort_order` int NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

--


-- --------------------------------------------------------

--
-- Table structure for table `mailing_list`
--


--

-- --------------------------------------------------------

--
-- Table structure for table `menupages`
--

CREATE TABLE `menupages` (
  `ID` int NOT NULL,
  `Name` text NOT NULL,
  `URL` text NOT NULL,
  `sort_order` int NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

--


-- --------------------------------------------------------

--
-- Table structure for table `pcnames`
--


--

-- --------------------------------------------------------

--
-- Table structure for table `reminders`
--


--

-- --------------------------------------------------------

--
-- Table structure for table `requests`
--


--

-- --------------------------------------------------------

--
-- Table structure for table `time`
--


--

-- --------------------------------------------------------

--
-- Table structure for table `users`
--


--

-- --------------------------------------------------------

--
-- Table structure for table `verifications`
--

CREATE TABLE `verifications` (
  `ID` int NOT NULL,
  `TypeID` int NOT NULL,
  `Code` text NOT NULL,
  `Expiry` datetime NOT NULL,
  `Current` text NOT NULL,
  `UserID` int NOT NULL,
  `IP` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

--


-- --------------------------------------------------------

--
-- Table structure for table `verificationtypes`
--

CREATE TABLE `verificationtypes` (
  `TypeID` int NOT NULL,
  `Type` text NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;

--


--
-- Indexes for dumped tables
--

--

--
-- Indexes for table `code`
--
ALTER TABLE `code`
  ADD PRIMARY KEY (`ID`);

--
-- Indexes for table `feedback`
--
ALTER TABLE `feedback`
  ADD PRIMARY KEY (`FeedbackID`);

--
-- Indexes for table `links`
--
ALTER TABLE `links`
  ADD PRIMARY KEY (`ID`);

--

--
-- Indexes for table `menupages`
--
ALTER TABLE `menupages`
  ADD PRIMARY KEY (`ID`);

--

--

--

--

--

--
-- Indexes for table `verifications`
--
ALTER TABLE `verifications`
  ADD PRIMARY KEY (`ID`),
  ADD KEY `TypeID` (`TypeID`);

--
-- Indexes for table `verificationtypes`
--
ALTER TABLE `verificationtypes`
  ADD PRIMARY KEY (`TypeID`);

--
-- AUTO_INCREMENT for dumped tables
--

--
-- AUTO_INCREMENT for table `code`
--
ALTER TABLE `code`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=4;

--
-- AUTO_INCREMENT for table `feedback`
--
ALTER TABLE `feedback`
  MODIFY `FeedbackID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=7;

--
-- AUTO_INCREMENT for table `links`
--
ALTER TABLE `links`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=12;

--
-- AUTO_INCREMENT for table `mailing_list`
--

--
-- AUTO_INCREMENT for table `pcnames`
--

--
-- AUTO_INCREMENT for table `reminders`
--

--
-- AUTO_INCREMENT for table `requests`
--

--
-- AUTO_INCREMENT for table `time`
--

--
-- AUTO_INCREMENT for table `users`
--

--
-- AUTO_INCREMENT for table `verifications`
--
ALTER TABLE `verifications`
  MODIFY `ID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=24;

--
-- AUTO_INCREMENT for table `verificationtypes`
--
ALTER TABLE `verificationtypes`
  MODIFY `TypeID` int NOT NULL AUTO_INCREMENT, AUTO_INCREMENT=2;
COMMIT;

/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;

SET FOREIGN_KEY_CHECKS = 1;